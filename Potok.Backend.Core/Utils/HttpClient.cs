using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Potok.Backend.Core.Utils;

public class HttpService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpService> _logger;

    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public HttpService(IHttpClientFactory httpClientFactory, ILogger<HttpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetStringAsync(string url, RequestOptions? options = null)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, options);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        return await ReadContentAsync(response, options);
    }

    public async Task<T?> GetJsonAsync<T>(string url, RequestOptions? options = null)
    {
        var json = await GetStringAsync(url, options);
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize JSON from {Url}", url);
            return default;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string url, RequestOptions? options = null)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, options);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsByteArrayAsync(options?.CancellationToken ?? default);
    }

    public async Task<HttpResponseMessage> GetResponseAsync(string url, RequestOptions? options = null)
    {
        return await SendAsync(HttpMethod.Get, url, null, options, HttpCompletionOption.ResponseHeadersRead);
    }

    public async Task<string> PostAsync(string url, HttpContent content, RequestOptions? options = null)
    {
        using var response = await SendAsync(HttpMethod.Post, url, content, options);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        return await ReadContentAsync(response, options);
    }
    
    public async Task<HttpResponseMessage> PostResponseAsync(string url, HttpContent? content, RequestOptions? options = null)
    {
        return await SendAsync(HttpMethod.Post, url, content, options, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, 
        string url, 
        HttpContent? content, 
        RequestOptions? options,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        options ??= RequestOptions.Default;
        
        var request = new HttpRequestMessage(method, url);
        
        if (content != null)
            request.Content = content;

        string clientName = options.UseProxy ? "Default" : "NoProxy";
        if (!options.AllowAutoRedirect)
            clientName += "NoRedirect";
            
        var client = _httpClientFactory.CreateClient(clientName);

        if (!string.IsNullOrEmpty(options.Cookie))
            request.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
            
        if (!string.IsNullOrEmpty(options.Referer))
            request.Headers.TryAddWithoutValidation("Referer", options.Referer);

        if (options.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            return await client.SendAsync(request, completionOption, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request to {Url} timed out after {Seconds}s", url, options.TimeoutSeconds);
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout) { RequestMessage = request };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request to {Url} failed", url);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request };
        }
    }

    private async Task<string> ReadContentAsync(HttpResponseMessage response, RequestOptions? options)
    {
        options ??= RequestOptions.Default;
        
        try
        {
            if (response.Content.Headers.ContentLength > options.MaxResponseSizeBytes)
            {
                _logger.LogWarning("Response from {Url} is too large ({Size} bytes)", 
                    response.RequestMessage?.RequestUri, response.Content.Headers.ContentLength);
                return string.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(options.CancellationToken);
            using var reader = new StreamReader(stream, options.Encoding);
            
            var buffer = new char[4096];
            var sb = new StringBuilder();
            var totalRead = 0;
            int read;

            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > options.MaxResponseSizeBytes)
                {
                    _logger.LogWarning("Response limit exceeded while reading from {Url}", response.RequestMessage?.RequestUri);
                    return string.Empty;
                }
                
                sb.Append(buffer, 0, read);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read content from {Url}", response.RequestMessage?.RequestUri);
            return string.Empty;
        }
    }
}