using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Entities.Gateway;
using Potok.Backend.Infrastructure.Configuration;
using Potok.Backend.Infrastructure.Gateway.Services;
using Serilog;

namespace Potok.Backend.CompositionTests;

public class TraktTokenServiceTests
{
    [Fact]
    public async Task GetValidAccessToken_ReturnsNull_WhenNoRow()
    {
        var users = new FakeUserRepository();
        var service = CreateService(users, new ScriptedHandler(_ => throw new InvalidOperationException("HTTP should not run")));

        var result = await service.GetValidAccessTokenAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsAccess_WhenNotExpired()
    {
        var userId = Guid.NewGuid();
        var users = new FakeUserRepository();
        users.Tokens[userId] = new UserTraktToken
        {
            UserId = userId,
            AccessToken = "live-access",
            RefreshToken = "refresh",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        var service = CreateService(users, new ScriptedHandler(_ => throw new InvalidOperationException("HTTP should not run")));

        var result = await service.GetValidAccessTokenAsync(userId);

        Assert.Equal("live-access", result);
        Assert.Equal(0, users.DeleteCount);
    }

    [Fact]
    public async Task GetValidAccessToken_RefreshesExpiredToken_AndSaves()
    {
        var userId = Guid.NewGuid();
        var users = new FakeUserRepository();
        users.Tokens[userId] = new UserTraktToken
        {
            UserId = userId,
            AccessToken = "expired-access",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };
        var handler = new ScriptedHandler(_ => JsonResponse("""
            {"access_token":"new-access","refresh_token":"refresh-2","expires_in":7776000}
            """));
        var service = CreateService(users, handler, clientSecret: "test-secret");

        var result = await service.GetValidAccessTokenAsync(userId);

        Assert.Equal("new-access", result);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"grant_type\":\"refresh_token\"", handler.LastBody);
        Assert.Contains("\"refresh_token\":\"refresh-1\"", handler.LastBody);
        Assert.Contains("\"client_secret\":\"test-secret\"", handler.LastBody);
        Assert.Contains("urn:ietf:wg:oauth:2.0:oob", handler.LastBody);
        var saved = users.Tokens[userId];
        Assert.Equal("new-access", saved.AccessToken);
        Assert.Equal("refresh-2", saved.RefreshToken);
        Assert.NotNull(saved.ExpiresAt);
        Assert.True(saved.ExpiresAt > DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task GetValidAccessToken_DeletesRow_WhenRefreshFails()
    {
        var userId = Guid.NewGuid();
        var users = new FakeUserRepository();
        users.Tokens[userId] = new UserTraktToken
        {
            UserId = userId,
            AccessToken = "expired-access",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        };
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = CreateService(users, handler);

        var result = await service.GetValidAccessTokenAsync(userId);

        Assert.Null(result);
        Assert.False(users.Tokens.ContainsKey(userId));
        Assert.Equal(1, users.DeleteCount);
    }

    [Fact]
    public async Task GetValidAccessToken_DeletesRow_WhenRefreshTokenMissing()
    {
        var userId = Guid.NewGuid();
        var users = new FakeUserRepository();
        users.Tokens[userId] = new UserTraktToken
        {
            UserId = userId,
            AccessToken = "expired-access",
            RefreshToken = null,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        };
        var service = CreateService(users, new ScriptedHandler(_ => throw new InvalidOperationException("HTTP should not run")));

        var result = await service.GetValidAccessTokenAsync(userId);

        Assert.Null(result);
        Assert.False(users.Tokens.ContainsKey(userId));
    }

    private static TraktTokenService CreateService(
        FakeUserRepository users,
        ScriptedHandler handler,
        string? clientSecret = "secret")
    {
        var options = Options.Create(new GatewayOptions
        {
            TraktClientId = "client-id",
            TraktClientSecret = clientSecret
        });
        return new TraktTokenService(
            users,
            new StaticHttpClientFactory(new HttpClient(handler)),
            options,
            new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StaticHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public string? LastBody { get; private set; }

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _respond(request);
        }
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        public Dictionary<Guid, UserTraktToken> Tokens { get; } = new();
        public int DeleteCount { get; private set; }

        public Task<User?> GetByIdAsync(Guid id) => throw new NotImplementedException();
        public Task<User?> GetByUsernameAsync(string username) => throw new NotImplementedException();
        public Task<User?> GetByTelegramIdAsync(long telegramId) => throw new NotImplementedException();
        public Task CreateAsync(User user) => throw new NotImplementedException();
        public Task UpdateSyncStrategyAsync(Guid userId, string strategy) => throw new NotImplementedException();
        public Task UpdatePasswordAsync(Guid userId, string passwordHash) => throw new NotImplementedException();
        public Task SetCredentialsAsync(Guid userId, string username, string passwordHash) => throw new NotImplementedException();
        public Task LinkTelegramAsync(Guid userId, long telegramId, string? telegramUsername) => throw new NotImplementedException();
        public Task UnlinkTelegramAsync(Guid userId) => throw new NotImplementedException();

        public Task<UserTraktToken?> GetTraktTokenAsync(Guid userId)
        {
            Tokens.TryGetValue(userId, out var token);
            if (token == null) return Task.FromResult<UserTraktToken?>(null);
            return Task.FromResult<UserTraktToken?>(new UserTraktToken
            {
                UserId = token.UserId,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAt = token.ExpiresAt
            });
        }

        public Task SaveTraktTokenAsync(UserTraktToken token)
        {
            Tokens[token.UserId] = token;
            return Task.CompletedTask;
        }

        public Task DeleteTraktTokenAsync(Guid userId)
        {
            DeleteCount++;
            Tokens.Remove(userId);
            return Task.CompletedTask;
        }
    }
}
