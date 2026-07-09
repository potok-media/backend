using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.SearchEngine;

public class UserSubscriptionItem
{
    [JsonPropertyName("tmdb_id")]
    public long TmdbId { get; set; }

    [JsonPropertyName("media")]
    public string Media { get; set; } = string.Empty;

    [JsonPropertyName("last_refresh_time")]
    public DateTimeOffset? LastRefreshTime { get; set; }
}
