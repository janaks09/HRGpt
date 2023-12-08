namespace HRGpt.Service.Abstracts
{
    public interface ISearchService
    {
        Task SearchAsync(string searchQuery);
    }
}
