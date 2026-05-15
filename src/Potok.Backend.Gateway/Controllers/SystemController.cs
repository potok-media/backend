using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class SystemController : ControllerBase
{
    [HttpGet("api/handshake")]
    public IActionResult Handshake()
    {
        return Ok(new { 
            name = "Potok Gateway", 
            version = "0.2.0",
            platform = "dotnet-10"
        });
    }

    [HttpPost("api/action/execute")]
    public IActionResult ExecuteAction([FromBody] SystemActionRequest request)
    {
        switch (request.Action)
        {
            case "clear-cache":
                // In memory cache clearing would go here
                return Ok(new { success = true, message = "Cache cleared" });
            
            default:
                return BadRequest(new { error = "UNKNOWN_ACTION", message = $"Action {request.Action} is not supported" });
        }
    }

    public record SystemActionRequest(string Action, object? Payload);
}
