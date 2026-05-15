namespace Potok.Backend.Core.Entities;

public class TorrentOverride
{
    public string Hash { get; set; } = string.Empty;
    public int? Season { get; set; }
    public int? EpisodeOffset { get; set; }
}
