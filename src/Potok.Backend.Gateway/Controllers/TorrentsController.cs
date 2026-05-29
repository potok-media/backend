using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ITorrentRepository _torrentRepository;
    private readonly IEventBroadcaster _eventBroadcaster;

    public TorrentsController(
        ITorrentRepository torrentRepository,
        IEventBroadcaster eventBroadcaster)
    {
        _torrentRepository = torrentRepository;
        _eventBroadcaster = eventBroadcaster;
    }

    [HttpGet("overrides/{hash}")]
    public async Task<IActionResult> GetOverride(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return BadRequest("Hash is required");
        var result = await _torrentRepository.GetOverrideAsync(hash.ToLower());
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("overrides/batch")]
    public async Task<IActionResult> GetBatchOverrides([FromBody] List<string> hashes)
    {
        if (hashes == null || !hashes.Any()) return Ok(new Dictionary<string, object>());
        var result = new Dictionary<string, object>();
        foreach (var hash in hashes)
        {
            if (string.IsNullOrEmpty(hash)) continue;
            var ov = await _torrentRepository.GetOverrideAsync(hash.ToLower());
            if (ov != null)
            {
                result[hash.ToLower()] = ov;
            }
        }
        return Ok(result);
    }

    [HttpPost("overrides")]
    public async Task<IActionResult> SaveOverride([FromBody] JsonElement body)
    {
        var hash = body.GetProperty("hash").GetString();
        var overrideObj = body.GetProperty("override");
        
        int? season = overrideObj.TryGetProperty("season", out var s) ? s.GetInt32() : null;
        int? offset = overrideObj.TryGetProperty("episodeOffset", out var o) ? o.GetInt32() : null;

        if (string.IsNullOrEmpty(hash)) return BadRequest("Hash is required");

        var cleanHash = hash.ToLower();

        await _torrentRepository.SetOverrideAsync(cleanHash, season, offset);
        _eventBroadcaster.Publish("override-updated", new { hash = cleanHash, season = season, episodeOffset = offset });
        return Ok(new { success = true });
    }
}
