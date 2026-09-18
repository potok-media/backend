using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.SearchEngine.Controllers;

[ApiController]
[Route("api/v1/torrents/continue")]
public class ContinueController : ControllerBase
{
    private const int ListLimit = 8;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    private readonly IContinueWatchingRepository _repo;

    public ContinueController(IContinueWatchingRepository repo)
    {
        _repo = repo;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await _repo.ListAsync(ListLimit, MaxAge);
        return Ok(new TorrentContinueListResponse(items));
    }

    [HttpGet("{mediaType}/{tmdbId:long}")]
    public async Task<IActionResult> Get(string mediaType, long tmdbId)
    {
        var item = await _repo.GetAsync(mediaType, tmdbId);
        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] TorrentContinueCursor body)
    {
        if (body == null || body.TmdbId <= 0 || string.IsNullOrWhiteSpace(body.FileIndex))
            return BadRequest("tmdbId and fileIndex are required");
        await _repo.UpsertAsync(body);
        return Ok(new { success = true });
    }

    [HttpPost("remove")]
    public async Task<IActionResult> Remove([FromBody] TorrentContinueRemoveRequest body)
    {
        if (body == null || body.TmdbId <= 0)
            return BadRequest("tmdbId is required");
        await _repo.DeleteAsync(body.MediaType, body.TmdbId);
        return Ok(new { success = true });
    }
}
