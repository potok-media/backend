using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Infrastructure.Gateway.Services;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly IHomeService _homeService;
    private readonly IMediaOrchestrator _orchestrator;
    private readonly TmdbClient _tmdbClient;
    private readonly ISettingsRepository _settings;
    private readonly ILogger _logger;

    public MediaController(IHomeService homeService, IMediaOrchestrator orchestrator, TmdbClient tmdbClient, ISettingsRepository settings, ILogger logger)
    {
        _homeService = homeService;
        _orchestrator = orchestrator;
        _tmdbClient = tmdbClient;
        _settings = settings;
        _logger = logger;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("home")]
    public async Task<IActionResult> GetHome(
        [FromQuery] string? cursor = null,
        [FromQuery] string posterSize = "w780",
        [FromQuery] string backdropSize = "original",
        [FromQuery] string logoSize = "original")
    {
        var result = await _homeService.GetHomeFeedAsync(cursor, BaseUrl, posterSize, backdropSize, logoSize);
        return Ok(result);
    }

    [HttpGet("detail/{mediaType}/{id}")]
    public async Task<IActionResult> GetDetail(
        string mediaType, 
        long id,
        [FromQuery] bool refresh = false)
    {
        var accessToken = await _settings.GetValueAsync("trakt_access_token");

        _logger.Debug("Fetching media details for {MediaType}/{Id} (refresh={Refresh}, hasToken={HasToken})", mediaType, id, refresh, !string.IsNullOrEmpty(accessToken));

        var result = await _orchestrator.GetMediaDetailAsync(mediaType, id, accessToken, BaseUrl);

        return result != null ? Ok(result) : NotFound();
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query)
    {
        var results = await _orchestrator.SearchAsync(query, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }

    [HttpGet("movies")]
    public async Task<IActionResult> GetMovies()
    {
        var results = await _orchestrator.GetPopularMoviesAsync(BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }

    [HttpGet("tvshows")]
    public async Task<IActionResult> GetTvShows()
    {
        var results = await _orchestrator.GetPopularTvShowsAsync(BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }

    [HttpGet("tmdb/tv/{tvId}/season/{seasonNumber}")]
    public async Task<IActionResult> GetSeason(long tvId, int seasonNumber)
    {
        var result = await _orchestrator.GetSeasonAsync(tvId, seasonNumber, BaseUrl);
        return result != null ? Ok(result) : NotFound();
    }

    [HttpGet("row/{id}")]
    public async Task<IActionResult> GetRow(string id, [FromQuery] int page = 1)
    {
        var results = await _orchestrator.GetMediaRowAsync(id, page, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }
}
