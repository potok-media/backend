using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly GatewayOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public HealthController(
        ISettingsRepository settingsRepository,
        IOptions<GatewayOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _settingsRepository = settingsRepository;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("bff")]
    public IActionResult HealthBff() => Ok();

    [HttpGet("search-engine")]
    public async Task<IActionResult> HealthSearchEngine()
    {
        var url = await _settingsRepository.GetValueAsync("searchEngineUrl");
        if (string.IsNullOrEmpty(url)) return StatusCode(501);

        return await ProxyHealthCheckAsync(url);
    }

    [HttpGet("torrent")]
    public async Task<IActionResult> HealthTorrent()
    {
        var json = await _settingsRepository.GetValueAsync("torrServer");
        string? url = null;
        
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("baseUrl", out var prop))
                {
                    url = prop.GetString();
                }
            }
            catch { /* Ignore parsing issues */ }
        }

        if (string.IsNullOrEmpty(url)) return StatusCode(501);

        return await ProxyHealthCheckAsync(url);
    }

    private async Task<IActionResult> ProxyHealthCheckAsync(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return StatusCode(501); // 501 Not Implemented (Not Configured)
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            
            var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/health");
            if (response.IsSuccessStatusCode)
            {
                return Ok();
            }
            
            return StatusCode(503); // 503 Service Unavailable
        }
        catch
        {
            return StatusCode(503);
        }
    }
}
