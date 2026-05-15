using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.Options;

/// <summary>
///     Настройки прокси-серверов.
/// </summary>
public class ProxySettings
{
    /// <summary>
    ///     Игнорировать прокси для локальных адресов.
    /// </summary>
    [ConfigurationKeyName("bypass-on-local")]
    public bool BypassOnLocal { get; set; }

    /// <summary>
    ///     Список прокси-серверов.
    /// </summary>
    [ConfigurationKeyName("list")]
    public List<ProxyItem> List { get; set; } = [];
}

public class ProxyItem
{
    /// <summary>
    ///     Адрес прокси-сервера (например, "http://proxy:8080").
    /// </summary>
    [ConfigurationKeyName("url")]
    public string Url { get; set; } = null!;

    /// <summary>
    ///     Имя пользователя для авторизации.
    /// </summary>
    [ConfigurationKeyName("username")]
    public string? Username { get; set; }

    /// <summary>
    ///     Пароль для авторизации.
    /// </summary>
    [ConfigurationKeyName("password")]
    public string? Password { get; set; }
}