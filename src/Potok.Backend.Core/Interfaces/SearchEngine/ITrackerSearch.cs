using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Models.SearchEngine.Details;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

/// <summary>
///     Поиск по конкретному трекеру.
/// </summary>
public interface ITrackerSearch
{
    TrackerType Tracker { get; }
    string TrackerName { get; }
    string Host { get; }

    /// <summary>
    ///     Выполняет поиск по строке запроса на выбранном трекере.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default);
}