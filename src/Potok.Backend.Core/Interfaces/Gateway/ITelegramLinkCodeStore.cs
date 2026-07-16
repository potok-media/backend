using System;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

// In-memory registry of pending Telegram deep-link codes. Shared between the poll endpoint (creates
// and reads codes) and the bot polling service (confirms them).
public interface ITelegramLinkCodeStore
{
    TelegramLinkCode Create(string purpose, Guid? userId, string language);
    TelegramLinkCode? Get(string code);
    bool Confirm(string code, long telegramId, string? telegramUsername);
    void Remove(string code);
}
