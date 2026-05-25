using System;

namespace Potok.Backend.Core.Entities;

public class UserListEntry
{
    public Guid UserId { get; set; }
    public string TmdbId { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty; // "movie" or "tv"
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
