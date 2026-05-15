using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

/// <summary>
///     Сервис для объединения дубликатов торрентов.
/// </summary>
public interface ITorrentMergerService
{
    /// <summary>
    ///     Объединяет список торрентов, удаляя дубликаты по InfoHash и суммируя метаданные.
    /// </summary>
    /// <param name="torrents">Исходный список торрентов.</param>
    /// <returns>Список уникальных торрентов.</returns>
    Task<List<TorrentDetails>> MergeAsync(IEnumerable<TorrentDetails> torrents);
}
