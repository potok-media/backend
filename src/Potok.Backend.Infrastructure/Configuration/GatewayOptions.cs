namespace Potok.Backend.Infrastructure.Configuration;

public record GatewayOptions
{
    public string TmdbApiKey { get; init; } = string.Empty;
    public string TraktClientId => "4346fe9c0e77439601db138d95c10f63a52e1edbf160f1c70407e1daaa11dadf";

    public bool MultiUserMode { get; init; } = false;
    public string JwtSecret { get; init; } = "default-fallback-gateway-jwt-secret-key-32-chars-long";
    public int JwtExpiryDays { get; init; } = 30;
    public string? AdminPassword { get; init; }
    public string AdminUsername { get; init; } = "admin";
    public bool AuthRequired => true;
}

