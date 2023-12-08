using HRGpt.Service.Abstracts;
using HRGpt.Service.Services;

namespace HRGpt.Web.Infrastructures.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static void AddApplicationServices(this IServiceCollection services)
        {
            services.AddSingleton<ISearchService, SearchService>();
        }

    }
}
