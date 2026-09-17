namespace Potok.Backend.Core.Models.SearchEngine.Api;

public class TorrentSearchQuery
{
    public long? TmdbId { get; set; }

    public string? Query { get; set; }

    /// <summary>
    ///     search
    /// </summary>
    public string Title { get; set; } = null!;

    /// <summary>
    ///     altname
    /// </summary>
    public string TitleOriginal { get; set; } = null!;

    /// <summary>
    ///     relased
    /// </summary>
    public int Year { get; set; }

    public Dictionary<string, string> Categories { get; set; } = new();
    public int IsSerial { get; set; } = -1;
    public string? UserAgent { get; set; }
    public string? QueryString { get; set; }
    public bool Exact { get; set; }
    public string? Type { get; set; }
    public string? Sort { get; set; }
    public string? Tracker { get; set; }
    public string? Voice { get; set; }
    public string? VideoType { get; set; }
    public int Quality { get; set; }
    public int Season { get; set; }
    public bool ForceSearch { get; set; } = false;
}