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
    private readonly IEventBroadcaster _eventBroadcaster;

    public SyncController(
        IUserHistoryRepository historyRepository,
        IUserListsRepository listsRepository,
        IEventBroadcaster eventBroadcaster)
    {
        _historyRepository = historyRepository;
        _listsRepository = listsRepository;
        _eventBroadcaster = eventBroadcaster;
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

        // Capture client ID and profile ID from headers for traceability (Pitfalls 1 & 17)
        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        // Publish sync:history:changed (for watched status)
        _eventBroadcaster.Publish("sync:history:changed", new
        {
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            seasonNumber = request.SeasonNumber,
            episodeNumber = request.EpisodeNumber,
            isWatched = isCompleted,
            actionSource = "playback_progress",
            senderId = senderId,
            profileId = profileId
        }, userId);

        // Publish sync:progress:changed
        _eventBroadcaster.Publish("sync:progress:changed", new
        {
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            seasonNumber = request.SeasonNumber,
            episodeNumber = request.EpisodeNumber,
            progressPercent = request.DurationSeconds > 0 ? ((double)request.ProgressSeconds / request.DurationSeconds) * 100 : 0,
            currentTime = (double)request.ProgressSeconds,
            duration = (double)request.DurationSeconds,
            isFinished = isCompleted,
            senderId = senderId,
            profileId = profileId
        }, userId);

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

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        _eventBroadcaster.Publish("sync:history:changed", new
        {
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            seasonNumber = request.SeasonNumber,
            episodeNumber = request.EpisodeNumber,
            isWatched = false,
            actionSource = "user_click",
            senderId = senderId,
            profileId = profileId
        }, userId);

        return Ok(new { success = true });
    }

    [HttpPost("history/bulk-progress")]
    public async Task<IActionResult> SaveBulkProgress([FromBody] BulkProgressRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.TmdbId) || string.IsNullOrWhiteSpace(request.MediaType))
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "TmdbId and MediaType are required" });
        }

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        // Perform save or remove in a loop in database (Pitfall 18: React Render Freeze bulk mitigation)
        foreach (var change in request.Changes)
        {
            if (change.IsWatched)
            {
                var historyEntry = new UserHistory
                {
                    Id = Guid.NewGuid(),
                    UserId = userId.Value,
                    TmdbId = request.TmdbId,
                    MediaType = request.MediaType,
                    SeasonNumber = change.SeasonNumber,
                    EpisodeNumber = change.EpisodeNumber,
                    ProgressSeconds = 100, // full watched state indicator
                    DurationSeconds = 100,
                    LastWatchedAt = DateTime.UtcNow
                };
                await _historyRepository.SaveProgressAsync(historyEntry);
            }
            else
            {
                await _historyRepository.DeleteProgressAsync(
                    userId.Value,
                    request.TmdbId,
                    request.MediaType,
                    change.SeasonNumber,
                    change.EpisodeNumber
                );
            }
        }

        // Broadcast a single batch event!
        _eventBroadcaster.Publish("sync:history:batch_changed", new
        {
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            changes = request.Changes,
            senderId = senderId,
            profileId = profileId
        }, userId);

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

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        _eventBroadcaster.Publish("sync:library:updated", new
        {
            listType = "favorites",
            action = "add",
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            senderId = senderId,
            profileId = profileId
        }, userId);

        return Ok(new { success = true });
    }

    [HttpPost("favorites/remove")]
    public async Task<IActionResult> RemoveFavorite([FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _listsRepository.RemoveFavoriteAsync(userId.Value, request.TmdbId, request.MediaType);

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        _eventBroadcaster.Publish("sync:library:updated", new
        {
            listType = "favorites",
            action = "remove",
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            senderId = senderId,
            profileId = profileId
        }, userId);

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

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        _eventBroadcaster.Publish("sync:library:updated", new
        {
            listType = "watchlist",
            action = "add",
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            senderId = senderId,
            profileId = profileId
        }, userId);

        return Ok(new { success = true });
    }

    [HttpPost("watchlist/remove")]
    public async Task<IActionResult> RemoveWatchlist([FromBody] WatchlistRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _listsRepository.RemoveFromWatchlistAsync(userId.Value, request.TmdbId, request.MediaType);

        var senderId = Request.Headers["X-Client-Id"].ToString();
        var profileId = Request.Headers["X-Profile-Id"].ToString();
        object mediaIdVal = int.TryParse(request.TmdbId, out var parsedId) ? parsedId : request.TmdbId;

        _eventBroadcaster.Publish("sync:library:updated", new
        {
            listType = "watchlist",
            action = "remove",
            mediaId = mediaIdVal,
            mediaType = request.MediaType,
            senderId = senderId,
            profileId = profileId
        }, userId);

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

public record BulkProgressRequest(
    string TmdbId,
    string MediaType,
    System.Collections.Generic.List<BulkProgressItem> Changes
);

public record BulkProgressItem(
    int SeasonNumber,
    int EpisodeNumber,
    bool IsWatched
);

public record FavoriteRequest(string TmdbId, string MediaType);
public record WatchlistRequest(string TmdbId, string MediaType);
