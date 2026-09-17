using Potok.Backend.Core.Models.SearchEngine.Api;
using Potok.Backend.Core.Models.SearchEngine.Details;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

public interface ISearchService
{
    /// <summary>
    ///     Универсальный поиск торрентов.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchTorrentsAsync(TorrentSearchQuery request);

    /// <summary>
    ///     Поиск для Jackett API v2.0
    /// </summary>
    Task<RootObject> SearchJackettAsync(TorrentSearchQuery request);
}
