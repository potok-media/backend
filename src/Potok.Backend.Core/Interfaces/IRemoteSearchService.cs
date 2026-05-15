using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

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