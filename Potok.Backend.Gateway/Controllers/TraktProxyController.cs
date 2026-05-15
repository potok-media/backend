using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class TraktProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayOptions _options;
    private readonly ISettingsRepository _settingsRepository;
    private const string TraktApiBase = "https://api.trakt.tv";

    public TraktProxyController(IHttpClientFactory httpClientFactory, IOptions<GatewayOptions> options, ISettingsRepository settingsRepository)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _settingsRepository = settingsRepository;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("TraktProxy");

    [HttpPost("api/trakt/oauth/device/code")]
    public async Task<IActionResult> GetDeviceCode()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync($"{TraktApiBase}/oauth/device/code", new { client_id = _options.TraktClientId });
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, errorContent);
        }

        var content = await response.Content.ReadAsStreamAsync();
        return File(content, "application/json");
    }

    [HttpPost("api/trakt/oauth/device/token")]
    public async Task<IActionResult> GetToken([FromBody] JsonElement body)
    {
        var client = CreateClient();
        var payload = new {
            code = body.GetProperty("code").GetString(),
            client_id = _options.TraktClientId,
            client_secret = _options.TraktClientSecret
        };
        var response = await client.PostAsJsonAsync($"{TraktApiBase}/oauth/device/token", payload);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, errorContent);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (json.TryGetProperty("access_token", out var tokenProp))
        {
            var token = tokenProp.GetString();
            if (!string.IsNullOrEmpty(token))
            {
                Console.WriteLine("[TraktProxyController] Saving access token to settings");
                await _settingsRepository.SetValueAsync("trakt_access_token", token);
            }
        }

        return Ok(json);
    }

    [HttpPost("api/trakt/logout")]
    public async Task<IActionResult> Logout()
    {
        Console.WriteLine("[TraktProxyController] Clearing Trakt access token from settings");
        await _settingsRepository.SetValueAsync("trakt_access_token", "");
        return Ok(new { success = true });
    }

    [Route("api/trakt/{*path}")]
    public async Task<IActionResult> ProxyTrakt(string path)
    {
        var client = CreateClient();
        var qs = Request.QueryString.Value;
        var url = $"{TraktApiBase}/{path}{qs}";

        var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), url);
        
        // Inject token from DB instead of client header
        var accessToken = await _settingsRepository.GetValueAsync("trakt_access_token");
        if (!string.IsNullOrEmpty(accessToken))
        {
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        foreach (var header in Request.Headers)
        {
            if (header.Key.StartsWith("trakt", StringComparison.OrdinalIgnoreCase))
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        if (Request.ContentLength > 0)
        {
            requestMessage.Content = new StreamContent(Request.Body);
            if (Request.ContentType != null)
            {
                requestMessage.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(Request.ContentType);
            }
        }

        var response = await client.SendAsync(requestMessage);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, errorContent);
        }

        var content = await response.Content.ReadAsStreamAsync();
        return File(content, response.Content.Headers.ContentType?.ToString() ?? "application/json");
    }
}
