using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Tracks;

namespace Potok.Backend.Core.Interfaces;

public interface ITorrentRepository
{
    Task AddOrUpdateAsync(IReadOnlyCollection<TorrentDetails> torrents);

    Task AddOrUpdateAsync<T>(IReadOnlyCollection<T> torrents,
        Func<T, CancellationToken, Task<bool>> predicate, CancellationToken ct)
        where T : TorrentDetails;

    Task<List<TorrentDetails>> GetForMediaProbeAsync(int limit, int maxAttempts,
        IReadOnlyCollection<string>? excludedTypes = null);

    Task UpdateMediaProbeAsync(string url, List<FfStream> ffprobe, HashSet<string>? languages);

    Task IncrementMediaProbeAttemptsAsync(string url);
    
    Task<TorrentOverride?> GetOverrideAsync(string hash);
    Task SetOverrideAsync(string hash, int? season, int? episodeOffset);
    Task EnsureDatabaseAsync();
}