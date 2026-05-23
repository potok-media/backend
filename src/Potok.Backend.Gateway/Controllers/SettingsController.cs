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
    public async Task<IActionResult> SyncSettings([FromBody] SyncSettingsRequest request)
    {
        if (request?.Settings == null) return BadRequest("Invalid settings payload");

        if (!string.IsNullOrEmpty(request.Settings.SearchEngineUrl))
        {
            await _settingsRepository.SetValueAsync("searchEngineUrl", request.Settings.SearchEngineUrl);
        }

        if (request.Settings.TorrServer != null)
        {
            var torrServerJson = JsonSerializer.Serialize(request.Settings.TorrServer, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await _settingsRepository.SetValueAsync("torrServer", torrServerJson);
        }

        return Ok(new { success = true });
    }
}

public record SyncSettingsRequest(SettingsPayload Settings);
public record SettingsPayload(string? SearchEngineUrl, TorrServerSettingsDto? TorrServer);
public record TorrServerSettingsDto(
    string? BaseUrl,
    bool AuthEnabled,
    string? AuthLogin,
    string? AuthPassword,
    bool? SaveToDb = true
);
