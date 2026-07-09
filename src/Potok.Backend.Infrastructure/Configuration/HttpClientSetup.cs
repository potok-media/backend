using System.Net;
using System.Security.Authentication;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.Configuration;

internal static class HttpClientSetup
{
    internal static void ApplyBrowserHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(TrackerHttpClient.DefaultUserAgent);
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.Add(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    internal static HttpClientHandler CreateHandler(bool allowAutoRedirect = true, IWebProxy? proxy = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            CheckCertificateRevocationList = false,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowAutoRedirect = allowAutoRedirect
        };

        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }
}