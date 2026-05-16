namespace Potok.Backend.Infrastructure.Configuration;

public record GatewayOptions
{
    public string TmdbApiKey { get; init; } = string.Empty;
    public string TraktClientId { get; init; } = string.Empty;
    public string TraktClientSecret { get; init; } = string.Empty;
    public string DefaultSearchEngineUrl { get; init; } = "http://127.0.0.1:6000";
    public string DefaultTorrServerUrl { get; init; } = "";
}
