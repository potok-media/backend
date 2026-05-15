using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Infrastructure.Gateway.Services;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly IHomeService _homeService;
    private readonly IMediaOrchestrator _orchestrator;
    private readonly TmdbClient _tmdbClient;
    private readonly ISettingsRepository _settings;

    public MediaController(IHomeService homeService, IMediaOrchestrator orchestrator, TmdbClient tmdbClient, ISettingsRepository settings)
    {
        _homeService = homeService;
        _orchestrator = orchestrator;
        _tmdbClient = tmdbClient;
        _settings = settings;
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
        string id,
        [FromQuery] bool refresh = false)
    {
        var accessToken = await _settings.GetValueAsync("trakt_access_token");

        Console.WriteLine($"[MediaController] GetDetail: {mediaType}/{id}, refresh={refresh}, hasToken={!string.IsNullOrEmpty(accessToken)}");

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
    public async Task<IActionResult> GetSeason(string tvId, int seasonNumber)
    {
        var result = await _orchestrator.GetSeasonAsync(tvId, seasonNumber, BaseUrl);
        return result != null ? Ok(result) : NotFound();
    }
}
