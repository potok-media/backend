using System;

namespace Potok.Backend.Core.Models.Gateway;

// Short-lived one-time code for the Telegram bot deep-link flow (used on HTTP deployments where the
// Login Widget cannot run). The frontend gets a code, opens t.me/<bot>?start=<code>; the bot polling
// service confirms it with the Telegram user's id; the frontend polls until confirmed.
public class TelegramLinkCode
{
    public string Code { get; init; } = string.Empty;

    // "auth" (login/register) or "link" (attach to an existing account).
    public string Purpose { get; init; } = "auth";

    // Set for the "link" purpose: the account the confirmed Telegram identity attaches to.
    public Guid? UserId { get; init; }

    // UI language of the requester (e.g. "ru", "en") so the bot replies in a matching language.
    public string Language { get; init; } = "en";

    public DateTime ExpiresAt { get; init; }

    public bool Confirmed { get; set; }
    public long TelegramId { get; set; }
    public string? TelegramUsername { get; set; }
}
