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
    private readonly ITorrServerClient _torrServerClient;
    private readonly ITorrentRepository _torrentRepository;
    private readonly IEventBroadcaster _eventBroadcaster;

    public TorrentsController(
        ISearchEngineClient searchEngineClient, 
        ITorrServerClient torrServerClient,
        ITorrentRepository torrentRepository,
        IEventBroadcaster eventBroadcaster)
    {
        _searchEngineClient = searchEngineClient;
        _torrServerClient = torrServerClient;
        _torrentRepository = torrentRepository;
        _eventBroadcaster = eventBroadcaster;
    }

    [HttpPost("search")]
    public async Task<IActionResult> SearchTorrents([FromBody] TorrentSearchRequest request)
    {
        var result = await _searchEngineClient.SearchAsync(request);
        return Ok(result);
    }

    [HttpPost("files")]
    public async Task<IActionResult> GetTorrentFiles([FromBody] TorrentFilesRequest request)
    {
        var result = await _torrServerClient.GetFilesAsync(request);
        return Ok(result);
    }

    [HttpPost("files/normalized")]
    public async Task<IActionResult> GetNormalizedFiles([FromBody] TorrentFilesRequest request)
    {
        var result = await _torrServerClient.GetNormalizedStreamUrlsAsync(request);
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

    [HttpPost("stream")]
    public async Task<IActionResult> GetStreamUrl([FromBody] TorrentStreamRequest request)
    {
        var result = await _torrServerClient.GetStreamUrlAsync(request);
        return Ok(result);
    }
}
