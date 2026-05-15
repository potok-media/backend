using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    private readonly ILibraryOrchestrator _orchestrator;
    private readonly ISettingsRepository _settings;

    public LibraryController(ILibraryOrchestrator orchestrator, ISettingsRepository settings)
    {
        _orchestrator = orchestrator;
        _settings = settings;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist() => await GetLibraryItems("watchlist", _orchestrator.GetWatchlistAsync);

    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites() => await GetLibraryItems("favorites", _orchestrator.GetFavoritesAsync);

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory() => await GetLibraryItems("history", _orchestrator.GetHistoryAsync);

    [HttpGet("calendar")]
    public async Task<IActionResult> GetCalendar() => await GetLibraryItems("calendar", _orchestrator.GetCalendarAsync);

    [HttpGet("up-next")]
    public async Task<IActionResult> GetUpNext() => await GetLibraryItems("up_next", _orchestrator.GetUpNextAsync);

    private async Task<IActionResult> GetLibraryItems(string key, Func<string, string, Task<IEnumerable<MediaCard>>> fetchFunc)
    {
        var accessToken = await _settings.GetValueAsync("trakt_access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("[LibraryController] Trakt access token not found in settings");
            return Unauthorized("Trakt not connected");
        }

        var results = await fetchFunc(accessToken, BaseUrl);
        return Ok(results ?? Enumerable.Empty<MediaCard>());
    }
}
