using Potok.Backend.Core.Entities.Gateway;

namespace Potok.Backend.Gateway.Controllers;

// Shared shape of the `user` object returned by auth endpoints. Kept in one place so login,
// register, /me, change-password/set-credentials and the Telegram endpoints stay in sync.
internal static class UserPayload
{
    public static object From(User user, bool traktConnected) => new
    {
        id = user.Id,
        username = user.Username,
        syncStrategy = user.SyncStrategy,
        traktConnected,
        telegramLinked = user.TelegramId != null,
        telegramUsername = user.TelegramUsername,
        hasPassword = !string.IsNullOrEmpty(user.PasswordHash),
    };
}
