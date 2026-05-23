using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

/// <summary>
///     Добавляет недостающие данные, которые невозможно вытащить со страницы поиска
/// </summary>
public interface ITrackerCatalogEnricher
{
    /// <summary>
    ///     Пытается обогатить раздачу, используя уже известные записи каталога.
    /// </summary>
    Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct);
}