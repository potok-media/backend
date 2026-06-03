using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    private readonly ILibraryOrchestrator _orchestrator;
    private readonly ILogger _logger;

    public LibraryController(ILibraryOrchestrator orchestrator, ILogger logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private string? GetTraktAccessToken()
    {
        var headerVal = Request.Headers["Trakt-Authorization"].ToString();
        if (string.IsNullOrEmpty(headerVal)) return null;
        if (headerVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return headerVal.Substring("Bearer ".Length).Trim();
        }
        return headerVal.Trim();
    }

    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist() => await GetLibraryItems("watchlist", _orchestrator.GetWatchlistAsync);

    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites() => await GetLibraryItems("favorites", _orchestrator.GetFavoritesAsync);

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory() => await GetLibraryItems("history", _orchestrator.GetHistoryAsync);

    [HttpGet("calendar")]
    public async Task<IActionResult> GetCalendar()
    {
        var accessToken = GetTraktAccessToken();
        var results = await _orchestrator.GetCalendarAsync(accessToken, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }

    [HttpGet("up-next")]
    public async Task<IActionResult> GetUpNext() => await GetLibraryItems("up_next", _orchestrator.GetUpNextAsync);

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var accessToken = GetTraktAccessToken();
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.Warning("Trakt access token not found in request headers when fetching user profile");
            return Unauthorized("Trakt not connected");
        }

        try
        {
            var profile = await _orchestrator.GetUserProfileAsync(accessToken);
            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch Trakt profile");
            return StatusCode(500, "Failed to fetch profile");
        }
    }

    private async Task<IActionResult> GetLibraryItems(string key, Func<string, string, Task<IEnumerable<MediaCard>>> fetchFunc)
    {
        var accessToken = GetTraktAccessToken();
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.Warning("Trakt access token not found in request headers when fetching library items");
            return Unauthorized("Trakt not connected");
        }

        var results = await fetchFunc(accessToken, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }
}
