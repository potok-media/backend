using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface ITelegramAuthService
{
    // Verify Telegram Login Widget data, then log in the linked account or (when MultiUserMode is on)
    // register a new password-less account for this Telegram identity.
    Task<TelegramAuthResult> AuthenticateAsync(IReadOnlyDictionary<string, string> authData);

    // Verify widget data and link this Telegram identity to an existing (authenticated) account.
    Task<TelegramAuthResult> LinkAsync(Guid userId, IReadOnlyDictionary<string, string> authData);

    Task UnlinkAsync(Guid userId);

    // Deep-link (bot) flow: the Telegram identity is already verified by the bot poller, so these
    // resolve a confirmed telegram id directly (no widget hash to check).
    Task<TelegramAuthResult> AuthenticateByTelegramIdAsync(long telegramId, string? telegramUsername);
    Task<TelegramAuthResult> LinkByTelegramIdAsync(Guid userId, long telegramId, string? telegramUsername);
}
