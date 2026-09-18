using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Entities.Gateway;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.Configuration;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TraktTokenService : ITraktTokenService
{
    private const string TokenEndpoint = "https://api.trakt.tv/oauth/token";
    private const string OobRedirectUri = "urn:ietf:wg:oauth:2.0:oob";
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();

    private readonly IUserRepository _userRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayOptions _options;
    private readonly ILogger _logger;

    public TraktTokenService(
        IUserRepository userRepository,
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayOptions> options,
        ILogger logger)
    {
        _userRepository = userRepository;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GetValidAccessTokenAsync(Guid userId, bool forceRefresh = false)
    {
        var token = await _userRepository.GetTraktTokenAsync(userId);
        if (token == null || string.IsNullOrEmpty(token.AccessToken))
        {
            return null;
        }

        if (!forceRefresh && !NeedsRefresh(token.ExpiresAt))
        {
            return token.AccessToken;
        }

        var gate = RefreshLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            token = await _userRepository.GetTraktTokenAsync(userId);
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                return null;
            }

            if (!forceRefresh && !NeedsRefresh(token.ExpiresAt))
            {
                return token.AccessToken;
            }

            return await RefreshAsync(userId, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsRefresh(DateTime? expiresAt)
    {
        if (expiresAt == null) return false;
        return expiresAt.Value <= DateTime.UtcNow.Add(RefreshSkew);
    }

    private async Task<string?> RefreshAsync(Guid userId, UserTraktToken token)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            _logger.Warning("Trakt token for user {UserId} has no refresh token; disconnecting", userId);
            await _userRepository.DeleteTraktTokenAsync(userId);
            return null;
        }

        try
        {
            var payload = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
                ["client_id"] = _options.TraktClientId,
                ["redirect_uri"] = OobRedirectUri
            };
            if (!string.IsNullOrWhiteSpace(_options.TraktClientSecret))
            {
                payload["client_secret"] = _options.TraktClientSecret;
            }

            var client = _httpClientFactory.CreateClient("TraktProxy");
            var response = await client.PostAsJsonAsync(TokenEndpoint, payload);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning(
                    "Trakt token refresh failed for user {UserId} with {StatusCode}; disconnecting",
                    userId,
                    (int)response.StatusCode);
                await _userRepository.DeleteTraktTokenAsync(userId);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var accessToken = json.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.Warning("Trakt token refresh for user {UserId} returned no access_token; disconnecting", userId);
                await _userRepository.DeleteTraktTokenAsync(userId);
                return null;
            }

            var refreshToken = json.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
            DateTime? expiresAt = null;
            if (json.TryGetProperty("expires_in", out var expProp) && expProp.ValueKind == JsonValueKind.Number)
            {
                expiresAt = DateTime.UtcNow.AddSeconds(expProp.GetInt64());
            }

            await _userRepository.SaveTraktTokenAsync(new UserTraktToken
            {
                UserId = userId,
                AccessToken = accessToken,
                RefreshToken = string.IsNullOrEmpty(refreshToken) ? token.RefreshToken : refreshToken,
                ExpiresAt = expiresAt
            });
            _logger.Information("Refreshed Trakt token for user {UserId}", userId);
            return accessToken;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Trakt token refresh error for user {UserId}", userId);
            return null;
        }
    }
}
