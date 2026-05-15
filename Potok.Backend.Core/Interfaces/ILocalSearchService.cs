using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

/// <summary>
///     Сервис высокого уровня для поиска раздач по названию или произвольной строке.
/// </summary>
public interface ILocalSearchService
{
    /// <summary>
    ///     Поиск по локализованному и оригинальному названию с опциональными фильтрами года/типа.
    /// </summary>
    Task<List<TorrentDetails>> SearchByTitleAsync(
        string? title,
        string? originalTitle,
        int? year = null,
        int? mediaType = null,
        bool exact = false);

    /// <summary>
    ///     Поиск по произвольной строке (с типом и точностью) среди всех трекеров/категорий.
    /// </summary>
    Task<List<TorrentDetails>> SearchByQueryAsync(
        string? query,
        int? mediaType = null,
        bool exact = false);

    /// <summary>
    ///     Быстрый поиск по TmdbId.
    /// </summary>
    Task<List<TorrentDetails>> SearchByTmdbIdAsync(long tmdbId);
}