using System.Text;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTor;

public class RuTorSearch : BaseRuTor
{
    private readonly ITorrentRepository _torrentRepository;

    public RuTorSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService,
        ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!Config.RuTor.EnableSearch)
            return [];

        var url = SearchUrl + Uri.EscapeDataString(query);
        var html = await HttpService.GetStringAsync(url, referer: url, encoding: Encoding.UTF8, ct: ct);

        if (string.IsNullOrWhiteSpace(html))
            return [];

        var torrents = Parse(html);

        var tasks = torrents.Select(async torrent =>
        {
            await _torrentRepository.AddOrUpdateAsync(
                [torrent],
                (t, token) => FetchDetailsAsync(t, token),
                ct);
        });

        await Task.WhenAll(tasks);

        return torrents.Where(t => t.Types?.Length > 0).ToList();
    }
}