using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

/// <summary>
///     Обогащает данные торрента: подтягивает метаданные и приводит их к единому формату.
/// </summary>
public interface ITorrentEnricher
{
    /// <summary>
    ///     Обогащает и конвертирует раздачу в внутренний формат приложения.
    /// </summary>
    TorrentDetails EnrichAndConvert(TorrentDetails torrent);
}