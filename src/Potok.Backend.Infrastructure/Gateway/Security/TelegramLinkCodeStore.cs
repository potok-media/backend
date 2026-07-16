using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Infrastructure.Gateway.Security;

// Thread-safe in-memory store for Telegram deep-link codes. Codes are short-lived one-time secrets;
// expired entries are pruned opportunistically on access.
public class TelegramLinkCodeStore : ITelegramLinkCodeStore
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TelegramLinkCode> _codes = new();

    public TelegramLinkCode Create(string purpose, Guid? userId, string language)
    {
        PruneExpired();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var entry = new TelegramLinkCode
        {
            Code = code,
            Purpose = purpose,
            UserId = userId,
            Language = string.IsNullOrWhiteSpace(language) ? "en" : language,
            ExpiresAt = DateTime.UtcNow.Add(CodeLifetime),
        };
        _codes[code] = entry;
        return entry;
    }

    public TelegramLinkCode? Get(string code)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryGetValue(code, out var entry)) return null;
        if (entry.ExpiresAt < DateTime.UtcNow)
        {
            _codes.TryRemove(code, out _);
            return null;
        }
        return entry;
    }

    public bool Confirm(string code, long telegramId, string? telegramUsername)
    {
        var entry = Get(code);
        if (entry == null) return false;
        entry.TelegramId = telegramId;
        entry.TelegramUsername = telegramUsername;
        entry.Confirmed = true;
        return true;
    }

    public void Remove(string code) => _codes.TryRemove(code, out _);

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _codes)
        {
            if (kv.Value.ExpiresAt < now) _codes.TryRemove(kv.Key, out _);
        }
    }
}
