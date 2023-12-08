using HRGpt.Service.Abstracts;
using HRGpt.Service.Models;
using HRGpt.Web.Models;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace HRGpt.Controllers
{
    public class HomeController : Controller
    {
        private readonly ISearchService _searchService;

        public HomeController(ISearchService searchService)
        {
            _searchService = searchService;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost("/api/ask")]
        public async Task<IActionResult> GetGptResponse([FromBody] UserInputModel model)
        {
            try
            {
                await _searchService.SearchAsync(model.Query);
                return Ok(new ApiResponse { Message = "Success" });
            }
            catch (Exception e)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, new ApiResponse { Message = e.Message });
            }

        }
    }
}
