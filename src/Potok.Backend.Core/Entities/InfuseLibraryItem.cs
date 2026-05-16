using System;

namespace Potok.Backend.Core.Entities;

public enum InfuseItemStatus
{
    Active = 0,
    Dead = 1,
    HashChanged = 2
}

public class InfuseLibraryItem
{
    public Guid Id { get; set; }
    public long TmdbId { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Poster { get; set; } = string.Empty;
    public string TorrentTitle { get; set; } = string.Empty;
    public string TorrentHash { get; set; } = string.Empty;
    public string MagnetUri { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public InfuseItemStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
