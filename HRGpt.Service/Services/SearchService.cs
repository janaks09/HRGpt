using HRGpt.Core.Constants;
using HRGpt.Core.Enums;
using HRGpt.Core.Helpers;
using HRGpt.Service.Abstracts;
using HRGpt.Service.Hubs;
using HRGpt.Service.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SharpToken;
using System.Net.Http.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace HRGpt.Service.Services
{
    public class SearchService : ISearchService
    {
        private readonly IConfiguration _configuration;
        private readonly IHubContext<GptResponseHub> _hubContext;
        private string _gptModel = OpenAIModels.GPT4;
        private static int _dataHeaderLength = "data: ".Length;

        public SearchService(IConfiguration configuration, IHubContext<GptResponseHub> hubContext)
        {
            _configuration = configuration;
            _hubContext = hubContext;
        }

        public async Task SearchAsync(string searchQuery)
        {
            var finalMessage= string.Empty;
            await foreach (var message in StreamChatCompletionRequestAsync(searchQuery))
            {
                Task.Delay(200).Wait();
                finalMessage += message;
                await _hubContext.Clients.All.SendAsync("gptresponse", finalMessage);
            }
        }



        #region Helpers (need to move this to different location)

        private async Task<EmbeddingResponseModel> CreateEmbeddingAsync(string query)
        {
            var embeddingModel = new EmbeddingModel
            {
                Model = OpenAIModels.TextEmbeddingAda002,
                Input = query
            };
            var request = new HttpRequestMessage()
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(OpenAIUri.EmbeddingUri),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Authorization", $"Bearer {_configuration["OpenAI:APIKey"]}" }
                },
                Content = JsonContent.Create(embeddingModel)
            };

            using var client = new HttpClient();
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var embeddingResponse = JsonConvert.DeserializeObject<EmbeddingResponseModel>(await response.Content.ReadAsStringAsync());
            return embeddingResponse;
        }

        private async Task<List<VectorObjectModel>> SearchDataFromVectorDatabaseAsync(EmbeddingResponseModel embeddingResponse)
        {
            var searchedEmbeebedList = new List<VectorObjectModel>();
            var embeddedData = embeddingResponse.Data.FirstOrDefault();
            var searchQuery = $"SELECT url, text, dot_product(vector, JSON_ARRAY_PACK(\"{JsonConvert.SerializeObject(embeddedData.embedding)}\")) as score\r\nfrom DocuVector\r\norder by score desc\r\nlimit 5;";
            string connectionString = _configuration["ConnectionStrings:SingleStoreDbConnection"];
            using MySqlConnection conn = new(connectionString);
            await conn.OpenAsync();

            using MySqlCommand cmd = new(searchQuery, conn);
            using MySqlDataReader reader = (MySqlDataReader)await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                searchedEmbeebedList.Add(new VectorObjectModel { Url = (string)reader[0], Text = (string)reader[1] });

            return searchedEmbeebedList;
        }

        private string BuildUserInputQueryMessage(string query, List<VectorObjectModel> relatedTexts, string model, int tokenBudget)
        {
            var introduction = "Use the below content to answer the subsequent question. Each content section has Related Url so list out the related url to at the end of response with heading \"Below are the related articles:\" If the answer cannot be found in the content, write \"I could not find an answer.\"";
            var question = $"\n\nQuestion: {query}";
            var message = introduction;

            foreach (var relatedText in relatedTexts)
            {
                var nextArticle = $"\n\n{relatedText.Text}\nRelated Url: {relatedText.Url}\n";
                if (NumTokens(message + nextArticle + question, model) > tokenBudget)
                    break;
                else
                    message += nextArticle;
            }
            return message + question;
        }

        private async IAsyncEnumerable<string> StreamChatCompletionRequestAsync(string searchquery)
        {
            var queryEmbeddingResponse = await CreateEmbeddingAsync(searchquery);
            var searchRelatedTexts = await SearchDataFromVectorDatabaseAsync(queryEmbeddingResponse);
            var userInputQueryMessage = BuildUserInputQueryMessage(searchquery, searchRelatedTexts, _gptModel, ModelTokenHelper.GetMaxTokenValue(_gptModel));

            var chatCompletionModel = new ChatCompletionModel
            {
                Model = _gptModel,
                MaxTokens = 500,
                Stream = true
            };

            chatCompletionModel.Messages.Add(new Message
            {
                Role = "system",
                Content = "You are a helpful assistant." //add more context as needed.
            });

            chatCompletionModel.Messages.Add(new Message
            {
                Role = "user",
                Content = userInputQueryMessage
            });

            var request = new HttpRequestMessage()
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(OpenAIUri.ChatCompletion),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Authorization", $"Bearer {_configuration["OpenAI:APIKey"]}" }
                },
                Content = JsonContent.Create(chatCompletionModel)
            };

            await foreach (var response in StreamChatCompletionsRaw(request))
            {
                var content = response.Choices[0].Delta?.Content;
                if (content is not null)
                    yield return content;
            }
        }

        private static (ProcessResponseEventResult result, ChatStreamResponseModel data) ProcessResponseEvent(string line)
        {
            if (line.StartsWith("data: "))
                line = line[_dataHeaderLength..];

            if (string.IsNullOrWhiteSpace(line)) return (ProcessResponseEventResult.Empty, default);

            if (line == "[DONE]")
                return (ProcessResponseEventResult.Done, default);

            var data = JsonSerializer.Deserialize<ChatStreamResponseModel>(line);
            if (data is null)
                throw new Exception($"Failed to deserialize response: {line} to type {typeof(ChatStreamResponseModel)}");

            return (ProcessResponseEventResult.Response, data);
        }

        private static async ValueTask<string> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            return await reader.ReadLineAsync(cancellationToken);
        }

        private static async IAsyncEnumerable<ChatStreamResponseModel> StreamChatCompletionsRaw(HttpRequestMessage httpRequest)
        {
            using var client = new HttpClient();
            var response = await client.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            while (await ReadLineAsync(reader, CancellationToken.None) is { } line) //streaming logic referencing from https://github.com/rodion-m/ChatGPT_API_dotnet project
            {
                var (result, data) = ProcessResponseEvent(line);
                switch (result)
                {
                    case ProcessResponseEventResult.Done:
                        yield break;
                    case ProcessResponseEventResult.Empty:
                        continue;
                    case ProcessResponseEventResult.Response:
                        yield return data!;
                        break;
                }
            }

            yield break;
        }

        private static int NumTokens(string text, string model)
        {
            var encoding = GptEncoding.GetEncodingForModel(model);
            var encodedText = encoding.Encode(text);
            return encodedText.Count;
        }

        #endregion
    }
}
