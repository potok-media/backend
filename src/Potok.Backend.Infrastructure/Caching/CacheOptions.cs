namespace Potok.Backend.Infrastructure.Caching;

/// <summary>
///     Минимальные настройки кэша приложения. Заменяет зависимость <see cref="CacheService" /> от
///     SearchEngine-типа Config (тот остался в potok-streaming). В Gateway кэш включён по умолчанию.
/// </summary>
public class CacheOptions
{
    public bool Enable { get; set; } = true;
}
