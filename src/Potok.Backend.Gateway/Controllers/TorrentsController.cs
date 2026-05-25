using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ISearchEngineClient _searchEngineClient;
    private readonly ITorrentRepository _torrentRepository;
    private readonly IEventBroadcaster _eventBroadcaster;

    public TorrentsController(
        ISearchEngineClient searchEngineClient, 
        ITorrentRepository torrentRepository,
        IEventBroadcaster eventBroadcaster)
    {
        _searchEngineClient = searchEngineClient;
        _torrentRepository = torrentRepository;
        _eventBroadcaster = eventBroadcaster;
    }

    [HttpPost("search")]
    public async Task<IActionResult> SearchTorrents([FromBody] TorrentSearchRequest request)
    {
        var result = await _searchEngineClient.SearchAsync(request);
        return Ok(result);
    }

    [HttpGet("overrides/{hash}")]
    public async Task<IActionResult> GetOverride(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return BadRequest("Hash is required");
        var result = await _torrentRepository.GetOverrideAsync(hash.ToLower());
        if (result == null) return NotFound();
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

        await _torrentRepository.SetOverrideAsync(hash, season, offset);
        _eventBroadcaster.Publish("override-updated", new { hash = hash, season = season, episodeOffset = offset });
        return Ok(new { success = true });
    }
}

