using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.Kinozal;

public class KinozalSearch : BaseKinozal
{
    private readonly ITorrentRepository _torrentRepository;

    public KinozalSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService,
        ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!Config.Kinozal.EnableSearch)
            return [];

        var url = $"{Host}/browse.php?s={query}&g=0&c=0&v=0&d=0&w=0&t=1&f=0";

        var html = await Get(url, RuEncoding, ct);
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var results = ParseBrowsePage(html, Host);

        if (results.Count == 0)
            return [];

        var tasks = results.Select(async torrent =>
        {
            await _torrentRepository.AddOrUpdateAsync(
                [torrent],
                (t, token) => FetchDetailsAsync(t, token),
                ct);
        });

        await Task.WhenAll(tasks);

        return results;
    }
}