using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Entities.Gateway;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Security;

// Verifies Telegram Login Widget payloads (HMAC-SHA256 over the data-check-string, keyed by
// SHA256(bot_token)) and resolves them to a gateway user: login for a linked account, register a
// new password-less account when MultiUserMode is on, or link to an existing account.
public class TelegramAuthService : ITelegramAuthService
{
    private const int MaxAuthAgeSeconds = 86400; // reject widget data older than 24h

    private readonly IUserRepository _userRepository;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly GatewayOptions _options;

    public TelegramAuthService(
        IUserRepository userRepository,
        IJwtTokenService jwtTokenService,
        IOptions<GatewayOptions> options)
    {
        _userRepository = userRepository;
        _jwtTokenService = jwtTokenService;
        _options = options.Value;
    }

    public async Task<TelegramAuthResult> AuthenticateAsync(IReadOnlyDictionary<string, string> authData)
    {
        if (!Verify(authData, out var telegramId, out var telegramUsername))
        {
            return TelegramAuthResult.Fail("INVALID_TELEGRAM_HASH");
        }
        return await AuthenticateByTelegramIdAsync(telegramId, telegramUsername);
    }

    public async Task<TelegramAuthResult> AuthenticateByTelegramIdAsync(long telegramId, string? telegramUsername)
    {
        var existing = await _userRepository.GetByTelegramIdAsync(telegramId);
        if (existing != null)
        {
            var token = _jwtTokenService.GenerateToken(existing.Id, existing.Username);
            var traktToken = await _userRepository.GetTraktTokenAsync(existing.Id);
            return TelegramAuthResult.Ok(token, existing, traktToken != null);
        }

        if (!_options.MultiUserMode)
        {
            return TelegramAuthResult.Fail("REGISTRATION_DISABLED");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = await GenerateUsernameAsync(telegramUsername, telegramId),
            PasswordHash = null,
            SyncStrategy = "none",
            CreatedAt = DateTime.UtcNow,
            TelegramId = telegramId,
            TelegramUsername = telegramUsername
        };
        await _userRepository.CreateAsync(user);

        var newToken = _jwtTokenService.GenerateToken(user.Id, user.Username);
        return TelegramAuthResult.Ok(newToken, user, traktConnected: false);
    }

    public async Task<TelegramAuthResult> LinkAsync(Guid userId, IReadOnlyDictionary<string, string> authData)
    {
        if (!Verify(authData, out var telegramId, out var telegramUsername))
        {
            return TelegramAuthResult.Fail("INVALID_TELEGRAM_HASH");
        }
        return await LinkByTelegramIdAsync(userId, telegramId, telegramUsername);
    }

    public async Task<TelegramAuthResult> LinkByTelegramIdAsync(Guid userId, long telegramId, string? telegramUsername)
    {
        var owner = await _userRepository.GetByTelegramIdAsync(telegramId);
        if (owner != null && owner.Id != userId)
        {
            return TelegramAuthResult.Fail("TELEGRAM_ALREADY_LINKED");
        }

        await _userRepository.LinkTelegramAsync(userId, telegramId, telegramUsername);

        var updated = await _userRepository.GetByIdAsync(userId);
        var traktToken = await _userRepository.GetTraktTokenAsync(userId);
        return TelegramAuthResult.Ok(token: null, updated!, traktToken != null);
    }

    public Task UnlinkAsync(Guid userId) => _userRepository.UnlinkTelegramAsync(userId);

    private async Task<string> GenerateUsernameAsync(string? telegramUsername, long telegramId)
    {
        var baseName = !string.IsNullOrWhiteSpace(telegramUsername) ? telegramUsername! : $"tg_{telegramId}";
        var candidate = baseName;
        var suffix = 1;
        while (await _userRepository.GetByUsernameAsync(candidate) != null)
        {
            candidate = $"{baseName}_{suffix++}";
        }
        return candidate;
    }

    // Validates the widget signature per https://core.telegram.org/widgets/login#checking-authorization.
    private bool Verify(IReadOnlyDictionary<string, string> authData, out long telegramId, out string? telegramUsername)
    {
        telegramId = 0;
        telegramUsername = null;

        if (string.IsNullOrWhiteSpace(_options.TelegramBotToken)) return false;
        if (!authData.TryGetValue("hash", out var providedHash) || string.IsNullOrEmpty(providedHash)) return false;
        if (!authData.TryGetValue("id", out var idStr) || !long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out telegramId)) return false;

        if (authData.TryGetValue("auth_date", out var authDateStr)
            && long.TryParse(authDateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authDate))
        {
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authDate;
            if (age > MaxAuthAgeSeconds || age < -MaxAuthAgeSeconds) return false;
        }
        else
        {
            return false;
        }

        var dataCheckString = string.Join("\n", authData
            .Where(kv => kv.Key != "hash")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = SHA256.HashData(Encoding.UTF8.GetBytes(_options.TelegramBotToken!));
        var computed = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedHex),
                Encoding.ASCII.GetBytes(providedHash.ToLowerInvariant())))
        {
            return false;
        }

        authData.TryGetValue("username", out telegramUsername);
        return true;
    }
}
