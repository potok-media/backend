using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.Api;

public class RootObject
{
    [JsonPropertyName("Results")] public List<Result> Results { get; set; }

    [JsonPropertyName("Error")] public string? Error { get; set; }
}