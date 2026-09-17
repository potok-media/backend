using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.Infrastructure.Http;

public class TrackerHttpClient
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    internal const string BrowserFetchedHeader = "X-Potok-Browser";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<Config> _config;
    private readonly CloudflareGuard _guard;
    private readonly IFlareSolverrClient _flareSolverr;
    private readonly ILogger<TrackerHttpClient> _logger;

    public TrackerHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<Config> config,
        CloudflareGuard guard,
        IFlareSolverrClient flareSolverr,
        ILogger<TrackerHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _guard = guard;
        _flareSolverr = flareSolverr;
        _logger = logger;
    }

    public async Task<string> GetStringAsync(
        string url,
        string? cookie = null,
        string? referer = null,
        Encoding? encoding = null,
        bool useProxy = true,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            url,
            content: null,
            cookie,
            referer,
            useProxy,
            allowRedirect: true,
            ct);

        if (!response.IsSuccessStatusCode)
            return string.Empty;

        if (IsBrowserFetched(response) || encoding is null || encoding == Encoding.UTF8)
            return await response.Content.ReadAsStringAsync(ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, encoding);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<HttpResponseMessage> PostResponseAsync(
        string url,
        HttpContent? content,
        string? cookie = null,
        string? referer = null,
        Encoding? encoding = null,
        bool useProxy = true,
        bool allowRedirect = true,
        CancellationToken ct = default)
    {
        return await SendAsync(HttpMethod.Post, url, content, cookie, referer, useProxy, allowRedirect, ct);
    }

    public async Task<string> PostStringAsync(
        string url,
        HttpContent? content,
        string? cookie = null,
        string? referer = null,
        Encoding? encoding = null,
        bool useProxy = true,
        CancellationToken ct = default)
    {
        using var response = await PostResponseAsync(url, content, cookie, referer, encoding, useProxy, true, ct);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        if (IsBrowserFetched(response) || encoding is null || encoding == Encoding.UTF8)
            return await response.Content.ReadAsStringAsync(ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, encoding);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string? cookie,
        string? referer,
        bool useProxy,
        bool allowRedirect,
        CancellationToken ct)
    {
        var host = TryGetHost(url);
        var flareEnabled = _config.CurrentValue.FlareSolverr.IsConfigured;
        var proxy = ResolveFlareProxy();
        string? postData = null;
        if (method == HttpMethod.Post && content is not null && flareEnabled)
            postData = await content.ReadAsStringAsync(ct);

        if (flareEnabled && host is not null && _guard.IsGuarded(host))
        {
            var viaBrowser = await FetchViaFlareSolverrAsync(method, url, postData, cookie, proxy, ct);
            if (viaBrowser is not null)
                return viaBrowser;

            return SynthesizeResponse(url, HttpStatusCode.InternalServerError, html: null, cookies: []);
        }

        var response = await SendDirectAsync(method, url, content, cookie, referer, useProxy, allowRedirect, ct);

        if (response.IsSuccessStatusCode)
        {
            if (host is not null)
                _guard.Unguard(host);
            return response;
        }

        if (!flareEnabled || host is null)
            return response;

        var challenge = CloudflareChallenge.IsChallenge(response);
        if (!challenge && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                challenge = CloudflareChallenge.IsChallengeBody(body);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read challenge body from {Host}", host);
            }
        }

        if (!challenge)
            return response;

        var solved = await FetchViaFlareSolverrAsync(method, url, postData, cookie, proxy, ct);
        if (solved is null)
            return response;

        _guard.MarkGuarded(host);
        response.Dispose();
        return solved;
    }

    private async Task<HttpResponseMessage> SendDirectAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string? cookie,
        string? referer,
        bool useProxy,
        bool allowRedirect,
        CancellationToken ct)
    {
        var clientName = useProxy ? "Default" : "NoProxy";
        if (!allowRedirect)
            clientName += "NoRedirect";

        var client = _httpClientFactory.CreateClient(clientName);
        var request = new HttpRequestMessage(method, url);
        if (content is not null)
            request.Content = content;
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrEmpty(referer))
            request.Headers.TryAddWithoutValidation("Referer", referer);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<HttpResponseMessage?> FetchViaFlareSolverrAsync(
        HttpMethod method,
        string url,
        string? postData,
        string? cookie,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        FlareSolverrSolution? solution;
        if (method == HttpMethod.Post)
        {
            solution = await _flareSolverr.PostAsync(url, postData, cookie, proxy, ct);
        }
        else
        {
            solution = await _flareSolverr.GetAsync(url, cookie, proxy, ct);
        }

        if (solution is null)
            return null;

        return SynthesizeResponse(url, (HttpStatusCode)solution.Status, solution.Html, solution.Cookies);
    }

    private FlareSolverrProxy? ResolveFlareProxy()
    {
        var list = _config.CurrentValue.Proxy.List;
        if (list is not { Count: > 0 })
            return null;

        var item = list[0];
        if (string.IsNullOrWhiteSpace(item.Url))
            return null;

        return new FlareSolverrProxy(item.Url, item.Username, item.Password);
    }

    internal static HttpResponseMessage SynthesizeResponse(
        string url,
        HttpStatusCode status,
        string? html,
        IReadOnlyList<FlareSolverrCookie> cookies)
    {
        var request = new HttpRequestMessage();
        try
        {
            request.RequestUri = new Uri(url);
        }
        catch (UriFormatException)
        {
            // leave RequestUri unset
        }

        var code = status == 0 ? HttpStatusCode.InternalServerError : status;
        var response = new HttpResponseMessage(code)
        {
            RequestMessage = request,
            Content = new StringContent(html ?? string.Empty, Encoding.UTF8, "text/html")
        };
        response.Headers.TryAddWithoutValidation(BrowserFetchedHeader, "1");

        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name))
                continue;
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"{cookie.Name}={cookie.Value}");
        }

        return response;
    }

    private static bool IsBrowserFetched(HttpResponseMessage response)
    {
        return response.Headers.Contains(BrowserFetchedHeader);
    }

    private static string? TryGetHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
