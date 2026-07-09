using System;

namespace Potok.Backend.Core.Entities.Gateway;

public class UserHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TmdbId { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty; // "movie" or "episode"
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public long ProgressSeconds { get; set; }
    public long DurationSeconds { get; set; }
    public DateTime LastWatchedAt { get; set; } = DateTime.UtcNow;
}
