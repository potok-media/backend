using System.Text;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.MegaPeer;

public class MegaPeerSearch : BaseMegaPeer
{
    private readonly ITorrentRepository _torrentRepository;

    public MegaPeerSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService, ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding("windows-1251");
        var encodedQuery = string.Join("", encoding.GetBytes(query).Select(b => $"%{b:X2}"));
        
        var url = $"{SearchUrl}?search={encodedQuery}&age=&cat=0&stype=0&sort=3&ascdesc=0";
        var html = await HttpService.GetStringAsync(url, referer: url, encoding: encoding, ct: ct);

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