using Potok.Backend.Core.Models.SearchEngine;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

public interface IQueriesRepository
{
    /// <summary>
    ///     Возвращает список популярных поисковых запросов.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetSearchQueriesAsync(int limit);

    /// <summary>
    ///     Возвращает список запросов, которые требуют обновления (last_refresh_time < olderThan или null).
    /// </summary>
    Task<IReadOnlyCollection<StaleQuery>> GetStaleSearchQueriesAsync(TimeSpan olderThan, int limit);

    /// <summary>
    ///     Сохраняет или обновляет статистику по поисковому запросу.
    /// </summary>
    Task TrackSearchQueryAsync(long tmdbId, string query);

    /// <summary>
    ///     Обновляет время последнего фонового обновления для запроса.
    /// </summary>
    Task UpdateLastRefreshTimeAsync(long tmdbId);

    /// <summary>
    ///     Удаляет поисковый запрос, если на него больше нет активных подписок.
    /// </summary>
    Task RemoveQueryIfNoSubscriptionsAsync(long tmdbId);

    /// <summary>
    ///     Возвращает список подписок пользователя с их временем обновления.
    /// </summary>
    Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid);
}