using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

/// <summary>
///     Параметры авторизации (логин, пароль, куки).
/// </summary>
public class Authorization
{
    /// <summary>
    ///     Логин пользователя.
    /// </summary>
    [ConfigurationKeyName("login")]
    public string Login { get; set; } = null!;

    /// <summary>
    ///     Пароль пользователя.
    /// </summary>
    [ConfigurationKeyName("password")]
    public string Password { get; set; } = null!;

    /// <summary>
    ///     (Опционально) Готовая строка Cookie для авторизации, если логин/пароль не используются или для 2FA.
    /// </summary>
    [ConfigurationKeyName("cookie")]
    public string? Cookie { get; set; }
}