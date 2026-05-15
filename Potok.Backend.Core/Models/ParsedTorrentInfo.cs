namespace Potok.Backend.Core.Models;

public class ParsedTorrentInfo
{
    public string? Resolution { get; set; }
    public string? Quality { get; set; }
    public string? Codec { get; set; }
    public string? Audio { get; set; }
    public string? Group { get; set; }
    public string? Container { get; set; }
    public string? Region { get; set; }
    public string? Language { get; set; }
    public int Year { get; set; }
    public int[]? Seasons { get; set; }
    public int[]? Episodes { get; set; }
    public bool IsComplete { get; set; }
}
