using System.Text.Json;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Infrastructure.SearchEngine.Services;

public class TorrentEnricher : ITorrentEnricher
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    ///     Обогащает данные торрента с помощью TorrentTitleParser.
    /// </summary>
    public TorrentDetails EnrichAndConvert(TorrentDetails torrent)
    {
        var parser = new TorrentTitleParser.Torrent(torrent.Title);

        torrent.ParsedInfo = new ParsedTorrentInfo
        {
            Resolution = parser.Resolution,
            Quality = parser.Quality,
            Codec = parser.Codec,
            Audio = parser.Audio,
            Group = parser.Group,
            Container = parser.Container,
            Region = parser.Region,
            Year = parser.Year,
            Seasons = parser.Season.HasValue ? new[] { parser.Season.Value } : null,
            Episodes = parser.Episode.HasValue ? new[] { parser.Episode.Value } : null,
            IsComplete = parser.Complete
        };

        // Сохраняем год для обратной совместимости
        if (parser.Year > 0)
            torrent.Relased = parser.Year;

        return torrent;
    }
}
