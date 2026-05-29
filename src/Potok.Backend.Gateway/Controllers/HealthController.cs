using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet("bff")]
    public IActionResult HealthBff() => Ok();
}
