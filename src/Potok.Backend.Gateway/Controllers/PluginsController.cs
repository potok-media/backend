using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Gateway.Services;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/plugins")]
public class PluginsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginsController> _logger;

    public PluginsController(IHttpClientFactory httpClientFactory, ILogger<PluginsController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
    
    /// <summary>
    /// Bundles a multi-file plugin (entry URL → single IIFE) via the internal
    /// loopback bundler sidecar. This is the bundler's ONLY door to the outside —
    /// the sidecar itself is never exposed.
    /// </summary>
    [HttpGet("bundle")]
    public async Task<IActionResult> Bundle([FromQuery] string entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry) ||
            !Uri.TryCreate(entry, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("invalid 'entry' url");
        }

        var client = _httpClientFactory.CreateClient(PluginBundlerConstants.HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/bundle?entry={Uri.EscapeDataString(entry)}");
        request.Headers.Add(PluginBundlerConstants.InternalHeader, PluginBundlerConstants.InternalKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin bundler unreachable.");
            return StatusCode(StatusCodes.Status502BadGateway, "bundler unreachable");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return StatusCode((int)response.StatusCode, error);
        }

        var js = await response.Content.ReadAsByteArrayAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return File(js, "application/javascript; charset=utf-8");
    }
}
