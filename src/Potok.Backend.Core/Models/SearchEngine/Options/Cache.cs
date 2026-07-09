using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

public class Cache
{
    /// <summary>
    ///     Включение кеша
    /// </summary>
    [ConfigurationKeyName("enable")]
    public bool Enable { get; set; } = true;

    /// <summary>
    ///     Время жизни кеша
    /// </summary>
    [ConfigurationKeyName("expiry")]
    public int Expiry { get; set; }

    /// <summary>
    ///     Время жизни кеша с авторизационными данными
    /// </summary>
    [ConfigurationKeyName("auth-expiry")]
    public int AuthExpiry { get; set; }
}