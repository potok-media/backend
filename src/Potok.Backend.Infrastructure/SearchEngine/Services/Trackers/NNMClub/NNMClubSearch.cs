using System.Text;
using System.Web;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.NNMClub;

public class NNMClubSearch : BaseNNMClub
{
    private readonly ITorrentRepository _torrentRepository;

    public NNMClubSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService,
        ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!Config.NNMClub.EnableSearch)
            return [];

        var parameters = GetSearchParameters(query);
        var url = $"{Host}/forum/tracker.php";

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding("windows-1251");

        var pairs = parameters.Select(kv =>
            $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value, encoding)}");
        var formEncoded = string.Join("&", pairs);

        var content = new StringContent(formEncoded, Encoding.UTF8, "application/x-www-form-urlencoded");

        var html = await HttpService.PostStringAsync(url, content, null, null, RuEncoding, true, ct);

        if (string.IsNullOrWhiteSpace(html))
            return [];

        var torrents = ParseTrackerPage(html, Host);

        var tasks = torrents.Select(async torrent =>
        {
            await _torrentRepository.AddOrUpdateAsync(
                [torrent],
                (t, token) => FetchDetailsAsync(t, token),
                ct);
        });

        await Task.WhenAll(tasks);

        return torrents;
    }
}