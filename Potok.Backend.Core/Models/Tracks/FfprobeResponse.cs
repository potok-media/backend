using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.Tracks;

public sealed class FfprobeResponse
{
    [JsonPropertyName("streams")] public List<FfStream>? Streams { get; set; }
}