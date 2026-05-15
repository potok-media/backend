using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.Tracks;

public sealed class FfTags
{
    [JsonPropertyName("language")] public string? Language { get; set; }

    [JsonPropertyName("BPS")] public string? Bps { get; set; }

    [JsonPropertyName("DURATION")] public string? Duration { get; set; }

    [JsonPropertyName("title")] public string? Title { get; set; }
}