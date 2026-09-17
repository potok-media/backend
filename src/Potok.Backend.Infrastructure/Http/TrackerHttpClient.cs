using System.Text;

namespace Potok.Backend.Infrastructure.Http;

public class TrackerHttpClient
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private readonly IHttpClientFactory _httpClientFactory;

    public TrackerHttpClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetStringAsync(
        string url, 
        string? cookie = null, 
        string? referer = null, 
        Encoding? encoding = null, 
        bool useProxy = true,
        CancellationToken ct = default)
    {
        var clientName = useProxy ? "Default" : "NoProxy";
        var client = _httpClientFactory.CreateClient(clientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        if (encoding == null || encoding == Encoding.UTF8)
        {
            return await response.Content.ReadAsStringAsync(ct);
        }

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
        var clientName = useProxy ? "Default" : "NoProxy";
        if (!allowRedirect) clientName += "NoRedirect";
        
        var client = _httpClientFactory.CreateClient(clientName);

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
        {
            return string.Empty;
        }
        
        if (encoding == null || encoding == Encoding.UTF8)
        {
            return await response.Content.ReadAsStringAsync(ct);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, encoding);
        return await reader.ReadToEndAsync(ct);
    }
}
