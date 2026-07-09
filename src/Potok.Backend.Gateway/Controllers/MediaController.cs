using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using Potok.Backend.Infrastructure.Gateway.Services;
using ILogger = Serilog.ILogger;
using System.Security.Claims;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly IHomeService _homeService;
    private readonly IMediaOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly IUserRepository _userRepository;

    public MediaController(
        IHomeService homeService,
        IMediaOrchestrator orchestrator,
        ILogger logger,
        IUserRepository userRepository)
    {
        _homeService = homeService;
        _orchestrator = orchestrator;
        _logger = logger;
        _userRepository = userRepository;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private async Task<string?> GetTraktAccessTokenAsync()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId)) return null;
        var token = await _userRepository.GetTraktTokenAsync(userId);
        return token?.AccessToken;
    }

    [HttpGet("home")]
    public async Task<IActionResult> GetHome(
        [FromQuery] string posterSize = "w780",
        [FromQuery] string backdropSize = "original",
        [FromQuery] string logoSize = "original")
    {
        var result = await _homeService.GetHomeFeedAsync(BaseUrl, posterSize, backdropSize, logoSize);
        return Ok(result);
    }

    [HttpGet("detail/{mediaType}/{id}")]
    public async Task<IActionResult> GetDetail(
        string mediaType, 
        long id,
        [FromQuery] bool refresh = false)
    {
        var accessToken = await GetTraktAccessTokenAsync();

        _logger.Debug("Fetching media details for {MediaType}/{Id} (refresh={Refresh}, hasToken={HasToken})", mediaType, id, refresh, !string.IsNullOrEmpty(accessToken));

        var result = await _orchestrator.GetMediaDetailAsync(mediaType, id, accessToken, BaseUrl);

        return result != null ? Ok(result) : NotFound();
    }

    [HttpGet("detail/{mediaType}/{id}/external_ids")]
    public async Task<IActionResult> GetExternalIds(string mediaType, long id)
    {
        var result = await _orchestrator.GetMediaDetailAsync(mediaType, id, null, BaseUrl);
        if (result == null) return NotFound();
        return Ok(new { kpId = result.KpId, imdbId = result.ImdbId });
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

    [HttpPost("batch")]
    public async Task<IActionResult> GetBatchDetails([FromBody] BatchMediaRequest request)
    {
        if (request?.Items == null || !request.Items.Any())
        {
            return Ok(Enumerable.Empty<MediaCard>());
        }

        var accessToken = await GetTraktAccessTokenAsync();

        var tasks = request.Items.Select(async item =>
        {
            try
            {
                return await _orchestrator.GetMediaDetailAsync(item.MediaType, item.TmdbId, accessToken, BaseUrl);
            }
            catch
            {
                return null;
            }
        });

        var cards = await Task.WhenAll(tasks);
        return Ok(cards.Where(c => c != null));
    }

}

public record BatchMediaRequest(System.Collections.Generic.List<BatchMediaItem> Items);
public record BatchMediaItem(long TmdbId, string MediaType);