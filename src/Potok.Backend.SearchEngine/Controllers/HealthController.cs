using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.SearchEngine.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok();
    }
}
