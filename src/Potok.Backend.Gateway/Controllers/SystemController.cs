using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class SystemController : ControllerBase
{
    private readonly IOptions<GatewayOptions> _options;

    public SystemController(IOptions<GatewayOptions> options)
    {
        _options = options;
    }

    [AllowAnonymous]
    [HttpGet("api/handshake")]
    public IActionResult Handshake()
    {
        return Ok(new
        {
            multiUserMode = _options.Value.MultiUserMode,
            authRequired = _options.Value.AuthRequired
        });
    }

    [HttpPost("api/action/execute")]
    public IActionResult ExecuteAction([FromBody] SystemActionRequest request)
    {
        switch (request.Action)
        {
            case "clear-cache":
                return Ok(new { success = true, message = "Cache cleared" });
            
            default:
                return BadRequest(new { error = "UNKNOWN_ACTION", message = $"Action {request.Action} is not supported" });
        }
    }

    public record SystemActionRequest(string Action, object? Payload);
}
