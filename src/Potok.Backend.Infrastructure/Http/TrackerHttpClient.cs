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

    private const int ProxyAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<Config> _config;
    private readonly CloudflareGuard _guard;
    private readonly IFlareSolverrClient _flareSolverr;
    private readonly TrackerProxyPool _pool;
    private readonly ILogger<TrackerHttpClient> _logger;

    public TrackerHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<Config> config,
        CloudflareGuard guard,
        IFlareSolverrClient flareSolverr,
        TrackerProxyPool pool,
        ILogger<TrackerHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _guard = guard;
        _flareSolverr = flareSolverr;
        _pool = pool;
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
        string? postData = null;
        string mediaType = "application/x-www-form-urlencoded";
        if (content is not null)
        {
            postData = await content.ReadAsStringAsync(ct);
            mediaType = content.Headers.ContentType?.MediaType ?? mediaType;
        }

        if (flareEnabled && host is not null && _guard.IsGuarded(host))
        {
            var viaBrowser = await FetchViaFlareSolverrAsync(method, url, postData, cookie, ct);
            if (viaBrowser is not null)
                return viaBrowser;

            return SynthesizeResponse(url, HttpStatusCode.InternalServerError, html: null, cookies: []);
        }

        var response = await SendDirectWithRetriesAsync(
            method, url, postData, mediaType, cookie, referer, useProxy, allowRedirect, host, ct);

        if (response.IsSuccessStatusCode)
            return response;

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

        var solved = await FetchViaFlareSolverrAsync(method, url, postData, cookie, ct);
        if (solved is null)
            return response;

        _guard.MarkGuarded(host);
        response.Dispose();
        return solved;
    }

    private async Task<HttpResponseMessage> SendDirectWithRetriesAsync(
        HttpMethod method,
        string url,
        string? postBody,
        string mediaType,
        string? cookie,
        string? referer,
        bool useProxy,
        bool allowRedirect,
        string? host,
        CancellationToken ct)
    {
        var attempts = 1;
        if (useProxy && _pool.HasProxies)
            attempts = Math.Min(ProxyAttempts, _pool.Items.Count);

        HttpResponseMessage? last = null;
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = useProxy ? _pool.Next() : null;
            try
            {
                using (RotatingWebProxy.Pin(endpoint))
                {
                    last?.Dispose();
                    last = await SendDirectOnceAsync(
                        method, url, postBody, mediaType, cookie, referer, useProxy, allowRedirect, ct);
                }

                return last;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                _logger.LogWarning(
                    "Tracker proxy {Proxy} failed for {Host}: {Reason}",
                    endpoint?.Url ?? "direct",
                    host ?? url,
                    ex.Message);

                if (i == attempts - 1)
                {
                    last?.Dispose();
                    return SynthesizeResponse(url, HttpStatusCode.ServiceUnavailable, html: null, cookies: []);
                }
            }
        }

        return last ?? SynthesizeResponse(url, HttpStatusCode.ServiceUnavailable, html: null, cookies: []);
    }

    private async Task<HttpResponseMessage> SendDirectOnceAsync(
        HttpMethod method,
        string url,
        string? postBody,
        string mediaType,
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
        if (postBody is not null)
            request.Content = new StringContent(postBody, Encoding.UTF8, mediaType);
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrEmpty(referer))
            request.Headers.TryAddWithoutValidation("Referer", referer);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        return ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;
    }

    private async Task<HttpResponseMessage?> FetchViaFlareSolverrAsync(
        HttpMethod method,
        string url,
        string? postData,
        string? cookie,
        CancellationToken ct)
    {
        FlareSolverrSolution? solution;
        if (method == HttpMethod.Post)
        {
            solution = await _flareSolverr.PostAsync(url, postData, cookie, proxy: null, ct);
        }
        else
        {
            solution = await _flareSolverr.GetAsync(url, cookie, proxy: null, ct);
        }

        if (solution is null)
            return null;

        return SynthesizeResponse(url, (HttpStatusCode)solution.Status, solution.Html, solution.Cookies);
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
