using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.SearchEngine.Controllers;

// Per-season torrent overrides — moved here from the gateway so the plugins get search + overrides from ONE
// service. GET returns the whole map (200 with {} if none, never 404); POST patches one source-season's mapping.
[ApiController]
[Route("api/v1/torrents/overrides")]
public class OverridesController : ControllerBase
{
    private readonly ISeasonOverrideRepository _repo;

    public OverridesController(ISeasonOverrideRepository repo)
    {
        _repo = repo;
    }

    [HttpGet("{hash}")]
    public async Task<IActionResult> Get(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return BadRequest("hash is required");
        var map = await _repo.GetAsync(hash.ToLower());
        return Ok(new TorrentOverrideMap(hash.ToLower(), map));
    }

    // Upsert one source-season's mapping. SourceSeason == null → the sentinel bucket "_" (files with no season).
    [HttpPost("{hash}/season")]
    public async Task<IActionResult> UpsertSeason(string hash, [FromBody] UpsertSeasonOverrideRequest body)
    {
        if (string.IsNullOrWhiteSpace(hash)) return BadRequest("hash is required");
        if (body == null) return BadRequest("body is required");
        var key = body.SourceSeason?.ToString() ?? "_";
        var map = await _repo.UpsertSeasonAsync(hash.ToLower(), key, new SeasonOverrideEntry(body.TargetSeason, body.Offset));
        return Ok(new { success = true, seasonMap = map });
    }

    // Reset ONE source-season's mapping (delete the entry → that season falls back to auto-parse). sourceSeason
    // absent/null → the sentinel bucket "_". POST (not DELETE) because the service's CORS allows POST/GET only.
    [HttpPost("{hash}/season/remove")]
    public async Task<IActionResult> RemoveSeason(string hash, int? sourceSeason)
    {
        if (string.IsNullOrWhiteSpace(hash)) return BadRequest("hash is required");
        var key = sourceSeason?.ToString() ?? "_";
        var map = await _repo.RemoveSeasonAsync(hash.ToLower(), key);
        return Ok(new { success = true, seasonMap = map });
    }

    // Replace the whole map (e.g. clear-all).
    [HttpPost("{hash}")]
    public async Task<IActionResult> Replace(string hash, [FromBody] TorrentOverrideMap body)
    {
        if (string.IsNullOrWhiteSpace(hash)) return BadRequest("hash is required");
        await _repo.ReplaceAsync(hash.ToLower(), body?.SeasonMap ?? new());
        return Ok(new { success = true });
    }
}
