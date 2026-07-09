using System.Net;

namespace Potok.Backend.Gateway.Security;

public static class ProxyRequestGuard
{
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
        "metadata.google",
        "kubernetes.default",
        "kubernetes.default.svc",
    };

    public static bool TryValidate(string url, out Uri? targetUri, out string error)
    {
        targetUri = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Url parameter is required";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "Invalid URL";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only HTTP and HTTPS targets are allowed";
            return false;
        }

        if (IsBlockedHost(parsed))
        {
            error = "Target host is not allowed";
            return false;
        }

        targetUri = parsed;
        return true;
    }

    internal static bool IsBlockedHost(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;
        if (BlockedHostnames.Contains(host))
        {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return IsPrivateOrReserved(address);
        }

        return false;
    }

    internal static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            };
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            // Unique local (fc00::/7)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }
        }

        return false;
    }
}