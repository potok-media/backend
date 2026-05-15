using System.Text.Json.Serialization;
using Potok.Backend.Core.Models.Tracks;

namespace Potok.Backend.Core.Models.Api;

/// <summary>
///     Модель результата поиска торрента, возвращаемого API.
/// </summary>
public class Result
{
    [JsonPropertyName("Tracker")] public string Tracker { get; set; } = null!;

    [JsonPropertyName("Details")] public string? Details { get; set; }

    [JsonPropertyName("Title")] public string Title { get; set; } = null!;

    [JsonPropertyName("Size")] public double Size { get; set; }

    [JsonPropertyName("PublishDate")] public DateTime PublishDate { get; set; }

    [JsonPropertyName("Category")] public HashSet<int> Category { get; set; } = [];

    [JsonPropertyName("CategoryDesc")] public string? CategoryDesc { get; set; }

    [JsonPropertyName("Seeders")] public int Seeders { get; set; }

    [JsonPropertyName("Peers")] public int Peers { get; set; }

    [JsonPropertyName("MagnetUri")] public string MagnetUri { get; set; } = null!;

    [JsonPropertyName("ffprobe")] public List<FfStream>? Ffprobe { get; set; }

    [JsonPropertyName("languages")] public HashSet<string> Languages { get; set; } = [];

    [JsonPropertyName("info")] public TorrentInfo? Info { get; set; }
}