using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

/// <summary>
/// HTTP/SOCKS proxies for tracker egress.
/// </summary>
public class ProxySettings
{
    [ConfigurationKeyName("bypass-on-local")]
    public bool BypassOnLocal { get; set; }

    /// <summary>
    /// Full proxy URLs, credentials in the URI:
    /// <c>socks5://user:pass@host:port</c> or <c>http://host:8080</c>.
    /// </summary>
    [ConfigurationKeyName("list")]
    public List<string> List { get; set; } = [];
}

public sealed record ProxyEndpoint(string Url, string? Username, string? Password)
{
    public Uri? ProxyUri => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri : null;

    public static ProxyEndpoint? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return null;

        var scheme = uri.Scheme;
        if (scheme is not ("http" or "https" or "socks4" or "socks5"))
            return null;
        if (string.IsNullOrWhiteSpace(uri.Host))
            return null;

        string? user = null;
        string? pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var cut = uri.UserInfo.IndexOf(':');
            if (cut < 0)
            {
                user = Uri.UnescapeDataString(uri.UserInfo);
            }
            else
            {
                user = Uri.UnescapeDataString(uri.UserInfo[..cut]);
                pass = Uri.UnescapeDataString(uri.UserInfo[(cut + 1)..]);
            }
        }

        var port = uri.Port > 0 ? $":{uri.Port}" : "";
        var url = $"{scheme}://{uri.Host}{port}";
        return new ProxyEndpoint(
            url,
            string.IsNullOrEmpty(user) ? null : user,
            string.IsNullOrEmpty(user) ? null : pass ?? "");
    }
}
