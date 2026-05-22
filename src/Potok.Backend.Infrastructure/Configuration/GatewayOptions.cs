namespace Potok.Backend.Infrastructure.Configuration;

public record GatewayOptions
{
    public string TmdbApiKey { get; init; } = string.Empty;
    public string TraktClientId => "4346fe9c0e77439601db138d95c10f63a52e1edbf160f1c70407e1daaa11dadf";
    public string DefaultSearchEngineUrl { get; init; } = "http://127.0.0.1:6000";
    public string DefaultTorrServerUrl { get; init; } = "";
}
