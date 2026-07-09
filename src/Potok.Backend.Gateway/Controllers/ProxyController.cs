using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using Potok.Backend.Gateway.Security;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[AllowAnonymous]
public class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    // Zero-dependency self-contained ConcurrentDictionary for high-performance HLS manifest caching
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType, DateTime Expiry)> _manifestCache = new();

    // Standard HTTP Content headers that must go to Content.Headers instead of HttpRequestMessage.Headers
    private static readonly HashSet<string> _contentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", 
        "Content-Length", "Content-Location", "Content-MD5", "Content-Range", 
        "Content-Type", "Expires", "Last-Modified"
    };

    public ProxyController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [Route("api/proxy")]
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD")]
    public async Task Proxy([FromQuery] string url, [FromQuery] string? referer = null, [FromQuery] string? origin = null)
    {
        if (!ProxyRequestGuard.TryValidate(url, out var targetUri, out var validationError))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync(validationError);
            return;
        }

        // 1. High-Performance HLS Manifest Cache Lookup (Skip downstream fetch + rewrite)
        var cacheKey = $"{url}|{referer}|{origin}";
        bool isGet = Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        bool looksLikeM3u8 = url.Contains(".m3u8") || url.Contains("mpegurl");

        if (isGet && looksLikeM3u8)
        {
            if (_manifestCache.TryGetValue(cacheKey, out var cacheEntry) && cacheEntry.Expiry > DateTime.UtcNow)
            {
                Response.StatusCode = 200;
                Response.Headers["Content-Type"] = cacheEntry.ContentType;
                Response.Headers["Content-Length"] = cacheEntry.Bytes.Length.ToString();
                
                // Allow cross-origin access for seamless player integration
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Access-Control-Allow-Headers"] = "*";
                Response.Headers["Access-Control-Allow-Methods"] = "*";

                await Response.Body.WriteAsync(cacheEntry.Bytes, 0, cacheEntry.Bytes.Length, HttpContext.RequestAborted);
                return;
            }
        }

        try
        {
            var client = _httpClientFactory.CreateClient("GatewayProxy");
            var validatedUri = targetUri!;
            var request = new HttpRequestMessage(new HttpMethod(Request.Method), validatedUri);

            // 2. Strict Header Splitting: Copy request headers (skip content headers to prevent exceptions)
            foreach (var header in Request.Headers)
            {
                var key = header.Key;
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_contentHeaders.Contains(key))
                {
                    continue; // Handled separately during request body copy
                }

                request.Headers.TryAddWithoutValidation(key, header.Value.ToArray());
            }

            // Spoof Referer/Origin if provided as query params
            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }
            else if (!request.Headers.Contains("Referer"))
            {
                request.Headers.Referrer = new Uri($"{validatedUri.Scheme}://{validatedUri.Host}/");
            }

            if (!string.IsNullOrEmpty(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            // 3. Request Content Copy: Properly bind content headers to the HttpRequestMessage Content
            if (Request.ContentLength > 0 || (Request.ContentType != null && !isGet))
            {
                request.Content = new StreamContent(Request.Body);
                foreach (var header in Request.Headers)
                {
                    var key = header.Key;
                    if (_contentHeaders.Contains(key))
                    {
                        request.Content.Headers.TryAddWithoutValidation(key, header.Value.ToArray());
                    }
                }
            }

            // 4. Send Request with Cancellation Token (Instant teardown if browser aborts/closes)
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

            Response.StatusCode = (int)response.StatusCode;

            // 5. Copy Response Headers Back (Exclude standard Kestrel-conflicting Hop-by-Hop headers)
            foreach (var header in response.Headers)
            {
                var key = header.Key;
                if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Response.Headers[key] = header.Value.ToArray();
            }

            // Copy Response Content Headers (Forward Content-Range for full partial range support)
            foreach (var header in response.Content.Headers)
            {
                var key = header.Key;
                if (key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || 
                    key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers[key] = header.Value.ToArray();
                }
            }

            // Explicitly set wildcard CORS headers to avoid target server conflicts and guarantee client-side playback success
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["Access-Control-Allow-Headers"] = "*";
            Response.Headers["Access-Control-Allow-Methods"] = "*";

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
            bool isM3u8 = contentType.Contains("mpegurl") || contentType.Contains("x-mpegurl") || url.Contains(".m3u8");

            if (isM3u8 && isGet)
            {
                // 6. Rewrite HLS playlists & Cache the rewritten bytes
                var content = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                var rewritten = RewriteM3u8(content, validatedUri, referer, origin);
                var bytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                
                var contentTypeStr = response.Content.Headers.ContentType?.ToString() ?? "application/x-mpegURL";
                _manifestCache[cacheKey] = (bytes, contentTypeStr, DateTime.UtcNow.AddSeconds(15));

                Response.Headers["Content-Length"] = bytes.Length.ToString();
                await Response.Body.WriteAsync(bytes, 0, bytes.Length, HttpContext.RequestAborted);
            }
            else
            {
                // 7. High-Performance Stream Forwarding using ArrayPool<byte> to completely eliminate LOH allocations
                using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                var buffer = ArrayPool<byte>.Shared.Rent(65536); // 64KB high-performance reusable buffer
                try
                {
                    int bytesRead;
                    while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer, 0, bytesRead, HttpContext.RequestAborted);
                    }
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Logging is unnecessary for normal browser disconnection/cancellations
        }
        catch (Exception ex)
        {
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync(ex.Message);
            }
        }
    }

    private string RewriteM3u8(string content, Uri baseUri, string? referer, string? origin)
    {
        var lines = content.Replace("\r", "").Split('\n');
        var proxyBase = $"{Request.Scheme}://{Request.Host}/api/proxy";
        var result = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                result.Add(line);
                continue;
            }

            if (trimmed.StartsWith("#"))
            {
                if (trimmed.Contains("URI=\""))
                {
                    result.Add(new Regex("URI=\"([^\"]+)\"").Replace(line, m =>
                    {
                        var abs = new Uri(baseUri, m.Groups[1].Value).ToString();
                        return $"URI=\"{proxyBase}?url={Uri.EscapeDataString(abs)}{(referer != null ? $"&referer={Uri.EscapeDataString(referer)}" : "")}{(origin != null ? $"&origin={Uri.EscapeDataString(origin)}" : "")}\"";
                    }));
                }
                else
                {
                    result.Add(line);
                }
            }
            else
            {
                try
                {
                    var abs = new Uri(baseUri, trimmed).ToString();
                    result.Add($"{proxyBase}?url={Uri.EscapeDataString(abs)}{(referer != null ? $"&referer={Uri.EscapeDataString(referer)}" : "")}{(origin != null ? $"&origin={Uri.EscapeDataString(origin)}" : "")}");
                }
                catch
                {
                    result.Add(line);
                }
            }
        }

        return string.Join("\n", result);
    }
}
