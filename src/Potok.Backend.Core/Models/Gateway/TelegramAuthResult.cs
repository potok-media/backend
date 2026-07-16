using Potok.Backend.Core.Entities.Gateway;

namespace Potok.Backend.Core.Models.Gateway;

// Outcome of a Telegram authenticate/link operation. On success carries the resolved user (and a
// JWT for the login flow); on failure carries a stable error code the controller maps to an HTTP
// response: INVALID_TELEGRAM_HASH, REGISTRATION_DISABLED, TELEGRAM_ALREADY_LINKED.
public class TelegramAuthResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Token { get; init; }
    public User? User { get; init; }
    public bool TraktConnected { get; init; }

    public static TelegramAuthResult Fail(string errorCode) => new() { Success = false, ErrorCode = errorCode };

    public static TelegramAuthResult Ok(string? token, User user, bool traktConnected) =>
        new() { Success = true, Token = token, User = user, TraktConnected = traktConnected };
}
