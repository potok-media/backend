using Potok.Backend.Core.Models.Api;
using Potok.Backend.Core.Models.Details;

namespace Potok.Backend.Core.Interfaces;

public interface ISearchService
{
    /// <summary>
    ///     Универсальный поиск торрентов.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchTorrentsAsync(TorrentSearchRequest request);

    /// <summary>
    ///     Поиск для Jackett API v2.0
    /// </summary>
    Task<RootObject> SearchJackettAsync(TorrentSearchRequest request);
}
