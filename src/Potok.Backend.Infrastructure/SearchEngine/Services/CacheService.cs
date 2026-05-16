using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;

namespace Potok.Backend.Infrastructure.SearchEngine.Services;

/// <summary>
///     Обертка над IMemoryCache для работы с кэшем приложения.
/// </summary>
public class CacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly Config _config;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public CacheService(IMemoryCache cache, IOptions<Config> config)
    {
        _cache = cache;
        _config = config.Value;
    }

    /// <summary>
    ///     Возвращает значение из кэша или создаёт и сохраняет его с заданным сроком жизни.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        if (!_config.Cache.Enable)
            return await factory();
        
        if (_cache.TryGetValue(key, out T cached)) return cached;

        var semaphore = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            if (_cache.TryGetValue(key, out T cachedSecondPass)) return cachedSecondPass;

            var result = await factory();

            var options = new MemoryCacheEntryOptions
            {
                Size = 1024 * 1024 * 150
            };

            if (expiry.HasValue)
                options.AbsoluteExpirationRelativeToNow = expiry.Value;
            else
                options.SlidingExpiration = TimeSpan.FromHours(1);

            _cache.Set(key, result, options);

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Удаляет значение из кэша по ключу.
    /// </summary>
    public async Task InvalidateAsync(string key)
    {
        _cache.Remove(key);
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Сохраняет значение в кэше с опциональным сроком жизни.
    /// </summary>
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        if (!_config.Cache.Enable)
            return;
        
        var options = new MemoryCacheEntryOptions
        {
            Size = 1024 * 1024 * 150
        };

        if (expiry.HasValue)
            options.AbsoluteExpirationRelativeToNow = expiry.Value;
        else
            options.SlidingExpiration = TimeSpan.FromHours(1);

        _cache.Set(key, value, options);

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Пытается получить значение из кэша без исключений на промахе.
    /// </summary>
    public bool TryGetValue<T>(string key, out T? value)
    {
        return _cache.TryGetValue(key, out value);
    }

    /// <summary>
    ///     Полностью очищает кэш путём принудительного компактирования.
    /// </summary>
    public async Task ClearAsync()
    {
        if (_cache is MemoryCache memoryCache) memoryCache.Compact(1.0);

        await Task.CompletedTask;
    }
}