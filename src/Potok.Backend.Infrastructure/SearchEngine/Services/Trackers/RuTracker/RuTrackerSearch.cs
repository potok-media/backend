using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;

public sealed class RuTrackerSearch : BaseRuTracker
{
    private readonly ITorrentRepository _torrentRepository;

    public RuTrackerSearch(IOptions<Config> config, HttpService httpService, ICacheService cacheService,
        ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query)
    {
        if (!Config.RuTracker.EnableSearch)
            return [];

        var results = new Dictionary<string, TorrentDetails>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        var url = BuildQueryUrl(Host, query, 0);
        var parsed = await FetchForumPageAsync(url, string.Empty, now);

        if (parsed.Count == 0)
            return new List<TorrentDetails>();

        foreach (var item in parsed)
            results[item.Url] = item;

        var tasks = results.Values.Select(async torrent =>
        {
            await _torrentRepository.AddOrUpdateAsync(
                [torrent],
                FetchDetailsAsync);
        });

        await Task.WhenAll(tasks);

        return results.Values.ToList();
    }
}