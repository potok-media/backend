using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Models.SearchEngine.Details;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

/// <summary>
///     Координатор поисковых запросов по набору поддерживаемых трекеров.
/// </summary>
public interface IRemoteSearchService
{
    /// <summary>
    ///     Возвращает список поддерживаемых трекеров.
    /// </summary>
    IReadOnlyCollection<TrackerType> GetSupportedTrackers();

    /// <summary>
    ///     Выполняет поиск по запросу в заданных трекерах или во всех доступных.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query,
        IReadOnlyCollection<TrackerType>? trackers = null);
}