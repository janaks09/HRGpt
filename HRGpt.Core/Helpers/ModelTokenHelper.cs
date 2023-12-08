using HRGpt.Core.Constants;

namespace HRGpt.Core.Helpers
{
    public static class ModelTokenHelper
    {
        static Dictionary<string, int> modelWithTokens = new()
        {
            { OpenAIModels.GPT4, 8192 },
            { OpenAIModels.GPT4_Turbo, 128000 }
        };

        public static int GetMaxTokenValue(string model)
        {
            if (modelWithTokens.ContainsKey(model))
                return modelWithTokens[model];
            else
                throw new Exception("Model not found");
        }
    }
}
