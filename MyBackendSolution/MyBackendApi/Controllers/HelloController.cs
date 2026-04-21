using Microsoft.AspNetCore.Mvc;

namespace MyBackendApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class HelloController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new { Message = "Hello from ASP.NET Core Backend!" });
        }
    }
}