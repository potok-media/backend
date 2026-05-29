using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController : ControllerBase
{
    private readonly IUserHistoryRepository _historyRepository;
    private readonly IUserListsRepository _listsRepository;

    public SyncController(
        IUserHistoryRepository historyRepository,
        IUserListsRepository listsRepository)
    {
        _historyRepository = historyRepository;
        _listsRepository = listsRepository;
    }

    private Guid? GetUserId()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            return null;
        }
        return userId;
    }

    // --- Playback History & Progress ---

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var history = await _historyRepository.GetByUserIdAsync(userId.Value);
        return Ok(history);
    }

    [HttpPost("history/progress")]
    public async Task<IActionResult> SaveProgress([FromBody] PlaybackProgressRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.TmdbId) || string.IsNullOrWhiteSpace(request.MediaType))
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "TmdbId and MediaType are required" });
        }

        // If progress is greater than 90%, we can treat it as fully watched (remove from active history or set progress = duration)
        bool isCompleted = request.DurationSeconds > 0 && ((double)request.ProgressSeconds / request.DurationSeconds) >= 0.90;

        var historyEntry = new UserHistory
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            TmdbId = request.TmdbId,
            MediaType = request.MediaType,
            SeasonNumber = request.SeasonNumber,
            EpisodeNumber = request.EpisodeNumber,
            ProgressSeconds = isCompleted ? request.DurationSeconds : request.ProgressSeconds,
            DurationSeconds = request.DurationSeconds,
            LastWatchedAt = DateTime.UtcNow
        };

        await _historyRepository.SaveProgressAsync(historyEntry);

        return Ok(new { success = true, isCompleted });
    }

    [HttpPost("history/remove")]
    public async Task<IActionResult> RemoveProgress([FromBody] RemoveProgressRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _historyRepository.DeleteProgressAsync(
            userId.Value,
            request.TmdbId,
            request.MediaType,
            request.SeasonNumber,
            request.EpisodeNumber
        );

        return Ok(new { success = true });
    }

    // --- Favorites ---

    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var favorites = await _listsRepository.GetFavoritesAsync(userId.Value);
        return Ok(favorites);
    }

    [HttpPost("favorites/add")]
    public async Task<IActionResult> AddFavorite([FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var entry = new UserListEntry
        {
            UserId = userId.Value,
            TmdbId = request.TmdbId,
            MediaType = request.MediaType,
            AddedAt = DateTime.UtcNow
        };

        await _listsRepository.AddFavoriteAsync(entry);
        return Ok(new { success = true });
    }

    [HttpPost("favorites/remove")]
    public async Task<IActionResult> RemoveFavorite([FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _listsRepository.RemoveFavoriteAsync(userId.Value, request.TmdbId, request.MediaType);
        return Ok(new { success = true });
    }

    // --- Watchlist ---

    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var watchlist = await _listsRepository.GetWatchlistAsync(userId.Value);
        return Ok(watchlist);
    }

    [HttpPost("watchlist/add")]
    public async Task<IActionResult> AddWatchlist([FromBody] WatchlistRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var entry = new UserListEntry
        {
            UserId = userId.Value,
            TmdbId = request.TmdbId,
            MediaType = request.MediaType,
            AddedAt = DateTime.UtcNow
        };

        await _listsRepository.AddToWatchlistAsync(entry);
        return Ok(new { success = true });
    }

    [HttpPost("watchlist/remove")]
    public async Task<IActionResult> RemoveWatchlist([FromBody] WatchlistRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _listsRepository.RemoveFromWatchlistAsync(userId.Value, request.TmdbId, request.MediaType);
        return Ok(new { success = true });
    }
}

public record PlaybackProgressRequest(
    string TmdbId,
    string MediaType,
    int? SeasonNumber,
    int? EpisodeNumber,
    long ProgressSeconds,
    long DurationSeconds
);

public record RemoveProgressRequest(
    string TmdbId,
    string MediaType,
    int? SeasonNumber,
    int? EpisodeNumber
);

public record FavoriteRequest(string TmdbId, string MediaType);
public record WatchlistRequest(string TmdbId, string MediaType);
