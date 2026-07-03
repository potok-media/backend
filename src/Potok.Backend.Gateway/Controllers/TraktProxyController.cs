using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class TraktProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayOptions _options;
    private readonly IUserRepository _userRepository;
    private readonly ILogger _logger;
    private const string TraktApiBase = "https://api.trakt.tv";

    public TraktProxyController(
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayOptions> options,
        IUserRepository userRepository,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _userRepository = userRepository;
        _logger = logger;
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
            client_id = _options.TraktClientId
        };
        var response = await client.PostAsJsonAsync($"{TraktApiBase}/oauth/device/token", payload);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, errorContent);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Save to DB if user is authenticated!
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out var userId))
        {
            try
            {
                var accessToken = json.GetProperty("access_token").GetString() ?? string.Empty;
                var refreshToken = json.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
                DateTime? expiresAt = null;
                if (json.TryGetProperty("expires_in", out var expProp) && expProp.ValueKind == JsonValueKind.Number)
                {
                    expiresAt = DateTime.UtcNow.AddSeconds(expProp.GetInt64());
                }

                if (!string.IsNullOrEmpty(accessToken))
                {
                    await _userRepository.SaveTraktTokenAsync(new UserTraktToken
                    {
                        UserId = userId,
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        ExpiresAt = expiresAt
                    });
                    _logger.Information("Saved Trakt token for user {UserId}", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save Trakt token for user {UserId} to database", userId);
            }
        }

        return Ok(json);
    }

    [HttpPost("api/trakt/logout")]
    public async Task<IActionResult> Logout()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out var userId))
        {
            try
            {
                await _userRepository.DeleteTraktTokenAsync(userId);
                _logger.Information("Deleted Trakt token for user {UserId} from database", userId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete Trakt token for user {UserId} from database", userId);
            }
        }

        return Ok(new { success = true });
    }

    [Route("api/trakt/{*path}")]
    public async Task<IActionResult> ProxyTrakt(string path)
    {
        var client = CreateClient();
        var qs = Request.QueryString.Value;
        var url = $"{TraktApiBase}/{path}{qs}";

        var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), url);
        
        string? accessToken = null;
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out var userId))
        {
            var token = await _userRepository.GetTraktTokenAsync(userId);
            accessToken = token?.AccessToken;
        }

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

        if (Request.ContentLength > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            requestMessage.Content = new ByteArrayContent(bytes);
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
