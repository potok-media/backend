using Potok.Backend.Core.Models.SearchEngine;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

public interface IContinueWatchingRepository
{
    Task<IReadOnlyList<TorrentContinueCursor>> ListAsync(int limit, TimeSpan maxAge);
    Task<TorrentContinueCursor?> GetAsync(string mediaType, long tmdbId);
    Task UpsertAsync(TorrentContinueCursor cursor);
    Task DeleteAsync(string mediaType, long tmdbId);
}
