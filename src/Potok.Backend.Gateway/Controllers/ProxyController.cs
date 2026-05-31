using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ProxyController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("api/proxy")]
    public async Task<IActionResult> Proxy([FromQuery] string url, [FromQuery] string? referer = null, [FromQuery] string? origin = null)
    {
        if (string.IsNullOrEmpty(url)) return BadRequest("Url parameter is required");

        try
        {
            var client = _httpClientFactory.CreateClient("Default");
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            // Forward Range header if present to support native video seeking
            if (Request.Headers.TryGetValue("Range", out var rangeHeader))
            {
                request.Headers.Add("Range", rangeHeader.ToString());
            }

            // Apply custom Referer or fallback to auto-generated host-based spoofing
            if (!string.IsNullOrEmpty(referer))
            {
                try
                {
                    request.Headers.Referrer = new System.Uri(referer);
                }
                catch
                {
                    request.Headers.TryAddWithoutValidation("Referer", referer);
                }
            }
            else
            {
                try
                {
                    var uri = new System.Uri(url);
                    request.Headers.Referrer = new System.Uri($"{uri.Scheme}://{uri.Host}/");
                }
                catch {}
            }

            // Forward custom Origin if present to bypass CDN origin validations
            if (!string.IsNullOrEmpty(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            // Send request with ResponseHeadersRead to stream data without buffering it in memory
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                return StatusCode((int)response.StatusCode);
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            
            bool isM3u8 = contentType.Contains("mpegurl") || contentType.Contains("x-mpegurl") || url.Contains(".m3u8");
            if (isM3u8)
            {
                var content = await response.Content.ReadAsStringAsync();
                var lines = content.Replace("\r", "").Split('\n');
                var rewrittenLines = new System.Collections.Generic.List<string>();
                var baseUri = new System.Uri(url);
                var proxyBaseUrl = $"{Request.Scheme}://{Request.Host}/api/proxy";

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine))
                    {
                        rewrittenLines.Add(line);
                        continue;
                    }

                    if (trimmedLine.StartsWith("#"))
                    {
                        if (trimmedLine.Contains("URI=\""))
                        {
                            var regex = new System.Text.RegularExpressions.Regex("URI=\"([^\"]+)\"");
                            var rewrittenLine = regex.Replace(line, match =>
                            {
                                var innerUrl = match.Groups[1].Value;
                                try
                                {
                                    var absUrl = new System.Uri(baseUri, innerUrl).ToString();
                                    var proxied = $"{proxyBaseUrl}?url={System.Uri.EscapeDataString(absUrl)}";
                                    if (!string.IsNullOrEmpty(referer)) proxied += $"&referer={System.Uri.EscapeDataString(referer)}";
                                    if (!string.IsNullOrEmpty(origin)) proxied += $"&origin={System.Uri.EscapeDataString(origin)}";
                                    return $"URI=\"{proxied}\"";
                                }
                                catch
                                {
                                    return match.Value;
                                }
                            });
                            rewrittenLines.Add(rewrittenLine);
                        }
                        else
                        {
                            rewrittenLines.Add(line);
                        }
                    }
                    else
                    {
                        try
                        {
                            var absUrl = new System.Uri(baseUri, trimmedLine).ToString();
                            var proxied = $"{proxyBaseUrl}?url={System.Uri.EscapeDataString(absUrl)}";
                            if (!string.IsNullOrEmpty(referer)) proxied += $"&referer={System.Uri.EscapeDataString(referer)}";
                            if (!string.IsNullOrEmpty(origin)) proxied += $"&origin={System.Uri.EscapeDataString(origin)}";
                            rewrittenLines.Add(proxied);
                        }
                        catch
                        {
                            rewrittenLines.Add(line);
                        }
                    }
                }

                var rewrittenContent = string.Join("\n", rewrittenLines);
                
                Response.Headers.ContentType = contentType;
                foreach (var header in response.Content.Headers)
                {
                    if (header.Key != "Content-Length" && header.Key != "Content-Type")
                    {
                        Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }
                foreach (var header in response.Headers)
                {
                    if (header.Key == "Accept-Ranges" || header.Key == "Content-Range")
                    {
                        Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }
                return Content(rewrittenContent, contentType);
            }
            
            // Forward essential response headers back to client
            foreach (var header in response.Content.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Headers)
            {
                if (header.Key == "Accept-Ranges" || header.Key == "Content-Range")
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return new FileStreamResult(stream, contentType)
            {
                EnableRangeProcessing = true // ASP.NET Core native Range request processing
            };
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
