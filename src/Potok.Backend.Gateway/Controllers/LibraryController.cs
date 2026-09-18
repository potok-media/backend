using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    private readonly ILibraryOrchestrator _orchestrator;
    private readonly ITraktTokenService _traktTokenService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger _logger;

    public LibraryController(
        ILibraryOrchestrator orchestrator,
        ITraktTokenService traktTokenService,
        IUserRepository userRepository,
        ILogger logger)
    {
        _orchestrator = orchestrator;
        _traktTokenService = traktTokenService;
        _userRepository = userRepository;
        _logger = logger;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out userId);
    }

    private async Task<string?> GetTraktAccessTokenAsync(bool forceRefresh = false)
    {
        if (!TryGetUserId(out var userId)) return null;
        return await _traktTokenService.GetValidAccessTokenAsync(userId, forceRefresh);
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
        var accessToken = await GetTraktAccessTokenAsync();
        var results = await _orchestrator.GetCalendarAsync(accessToken, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }

    [HttpGet("up-next")]
    public async Task<IActionResult> GetUpNext() => await GetLibraryItems("up_next", _orchestrator.GetUpNextAsync);

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized("Trakt not connected");
        }

        var accessToken = await _traktTokenService.GetValidAccessTokenAsync(userId);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.Warning("Trakt access token not found for user when fetching user profile");
            return Unauthorized("Trakt not connected");
        }

        try
        {
            return Ok(await _orchestrator.GetUserProfileAsync(accessToken));
        }
        catch (TraktUnauthorizedException)
        {
            var retried = await _traktTokenService.GetValidAccessTokenAsync(userId, forceRefresh: true);
            if (string.IsNullOrEmpty(retried))
            {
                return Unauthorized("Trakt not connected");
            }

            try
            {
                return Ok(await _orchestrator.GetUserProfileAsync(retried));
            }
            catch (TraktUnauthorizedException)
            {
                await _userRepository.DeleteTraktTokenAsync(userId);
                return Unauthorized("Trakt not connected");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch Trakt profile");
            return StatusCode(500, "Failed to fetch profile");
        }
    }

    private async Task<IActionResult> GetLibraryItems(string _, Func<string, string, Task<IEnumerable<MediaCard>>> fetchFunc)
    {
        var accessToken = await GetTraktAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.Warning("Trakt access token not found for user when fetching library items");
            return Unauthorized("Trakt not connected");
        }

        try
        {
            var results = await fetchFunc(accessToken, BaseUrl);
            return Ok(results ?? Enumerable.Empty<MediaCard>());
        }
        catch (TraktUnauthorizedException)
        {
            return Unauthorized("Trakt not connected");
        }
    }
}
