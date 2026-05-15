using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsRepository _settingsRepository;

    public SettingsController(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    [HttpGet("get")]
    public async Task<IActionResult> GetSettings([FromQuery] string key)
    {
        var value = await _settingsRepository.GetValueAsync(key);
        if (value == null) return NotFound();

        try 
        {
            // Try to return as JSON object if possible
            var doc = JsonDocument.Parse(value);
            return Ok(doc.RootElement);
        }
        catch 
        {
            // Otherwise return as raw string
            return Ok(value);
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncSettings([FromBody] JsonElement payload)
    {
        // The client might send a wrapper { "settings": { ... }, "timestamp": ... }
        var settingsElement = payload;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("settings", out var sProp))
        {
            settingsElement = sProp;
        }

        if (settingsElement.ValueKind != JsonValueKind.Object) return BadRequest("Invalid settings payload");

        foreach (var property in settingsElement.EnumerateObject())
        {
            var key = property.Name;
            var value = property.Value.ValueKind == JsonValueKind.String 
                ? property.Value.GetString() 
                : property.Value.GetRawText();
            
            if (value != null)
            {
                await _settingsRepository.SetValueAsync(key, value);
            }
        }

        return Ok(new { success = true });
    }
}
