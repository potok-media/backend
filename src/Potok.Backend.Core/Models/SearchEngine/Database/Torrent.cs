namespace Potok.Backend.Core.Models.SearchEngine.Database;

/// <summary>
///     Модель хранения торрента в БД (новое поколение).
/// </summary>
public class Torrent
{
    public Guid Id { get; set; }
    
    public string InfoHash { get; set; } = null!;
    
    public long? TmdbId { get; set; }

    public string TrackerName { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string Url { get; set; } = null!;

    public long Size { get; set; }

    public string MagnetUri { get; set; } = null!;

    public int Seeders { get; set; }

    public int Leechers { get; set; }

    public DateTime PublishDate { get; set; }

    public string? ParsedInfo { get; set; } // JSONB

    public DateTimeOffset UpdatedAt { get; set; }
}
