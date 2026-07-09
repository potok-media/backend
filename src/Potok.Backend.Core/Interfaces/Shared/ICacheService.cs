namespace Potok.Backend.Core.Interfaces.Shared;

/// <summary>
///     Контракт кеша приложения: ленивое получение, явная запись и инвалидирование.
/// </summary>
public interface ICacheService
{
    /// <summary>
    ///     Возвращает значение по ключу или создаёт через фабрику и кладёт в кэш (с TTL при необходимости).
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null);

    /// <summary>
    ///     Удаляет запись из кэша, если она есть.
    /// </summary>
    Task InvalidateAsync(string key);

    /// <summary>
    ///     Сохраняет значение в кэш с опциональным временем жизни.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);

    /// <summary>
    ///     Пытается прочитать значение из кэша без выброса исключений на промахе.
    /// </summary>
    bool TryGetValue<T>(string key, out T? value);
}