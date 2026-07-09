using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.SearchEngine.Tracks;

public sealed class FfprobeResponse
{
    [JsonPropertyName("streams")] public List<FfStream>? Streams { get; set; }
}