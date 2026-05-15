using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class BootstrapController : ControllerBase
{
    [HttpGet("api/bootstrap")]
    public IActionResult GetBootstrap()
    {
        var response = new BootstrapResponse(
            ContractVersion: 1,
            EnabledActionGroups: new[] { "watch", "sources" },
            EnabledContextualSurfaces: new[] { "detail", "watch", "sources" },
            PrefsSchemaVersion: 1,
            ResolvedLocale: "ru",
            SafeUiFlags: new { }
        );

        return Ok(response);
    }
}
