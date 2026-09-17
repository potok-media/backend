using Potok.Backend.Core.Models.SearchEngine.Tracks;

namespace Potok.Backend.Core.Models.SearchEngine.Details;

/// <summary>
///     Базовая модель торрента.
/// </summary>
public class TorrentDetails : ICloneable
{
    public Guid Id { get; set; }

    public long? TmdbId { get; set; }

    public string? InfoHash { get; set; }

    public string TrackerName { get; set; } = null!;

    public string[]? Types { get; set; } = null!;

    public string Url { get; set; } = null!;

    public string Title { get; set; } = null!;

    public int Sid { get; set; }

    public int Pir { get; set; }

    public string? SizeName { get; set; } = null!;

    public DateTime CreateTime { get; set; } = DateTime.UtcNow;

    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;

    public DateTime CheckTime { get; set; } = DateTime.Now;

    public string? Magnet { get; set; } = null!;

    public string? Name { get; set; } = null!;

    public string? OriginalName { get; set; } = null!;

    public int Relased { get; set; }

    public HashSet<string>? Languages { get; set; }

    public string? SourceSeasonNumber { get; set; } = null!;

    public string? SourceSeasonOrder { get; set; } = null!;

    public double Size { get; set; }

    public int Quality { get; set; }

    public string? VideoType { get; set; } = null!;

    public HashSet<string>? Voices { get; set; }

    public HashSet<int>? Seasons { get; set; }

    public List<FfStream>? Ffprobe { get; set; }

    public int FfprobeAttempts { get; set; }

    public ParsedTorrentInfo? ParsedInfo { get; set; }

    public object Clone()
    {
        return MemberwiseClone();
    }
}