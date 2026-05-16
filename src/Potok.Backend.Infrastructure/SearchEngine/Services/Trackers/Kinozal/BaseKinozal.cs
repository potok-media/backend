using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.Kinozal;

public class BaseKinozal : BaseTrackerSearch, ITrackerCatalogEnricher
{
    private const string CookieKey = "kinozal:cookie";

    protected BaseKinozal(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService) : base(config,
        httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Kinozal;
    public override string TrackerName => "kinozal";
    public override string Host => "https://kinozal.tv";

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        var magnet = await FetchMagnetAsync(torrent.Url, ct);
        if (!string.IsNullOrWhiteSpace(magnet))
            torrent.Magnet = magnet;

        return !string.IsNullOrWhiteSpace(torrent.Magnet);
    }

    private async Task<string?> FetchMagnetAsync(string url, CancellationToken ct)
    {
        try
        {
            var idMatch = Regex.Match(url, @"id=(\d+)");
            if (!idMatch.Success)
                return null;

            var id = idMatch.Groups[1].Value;
            var detailsUrl = $"{Host}/get_srv_details.php?id={id}&action=2";

            var html = await Get(detailsUrl, null, ct);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var hashMatch = Regex.Match(html, @"Инфо хеш: ([A-Fa-f0-9]{40})");
            if (hashMatch.Success) return $"magnet:?xt=urn:btih:{hashMatch.Groups[1].Value}";

            return null;
        }
        catch
        {
            return null;
        }
    }

    protected async Task<string> Get(string url, Encoding? encoding, CancellationToken ct)
    {
        if (!CacheService.TryGetValue(CookieKey, out string? cookie))
            cookie = await Authorize(ct: ct);

        var html = await HttpService.GetStringAsync(url, cookie, null, encoding, true, ct);

        if (string.IsNullOrWhiteSpace(html) || html.Contains("Вход в систему"))
        {
            cookie = await Authorize(true, ct);
            html = await HttpService.GetStringAsync(url, cookie, null, encoding, true, ct);
        }

        return html;
    }

    private async Task<string> Authorize(bool reAuth = false, CancellationToken ct = default)
    {
        var login = Config.Kinozal.Authorization.Login;
        var password = Config.Kinozal.Authorization.Password;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return string.Empty;

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", login },
            { "password", password },
            { "returnto", "" }
        });

        var response = await HttpService.PostResponseAsync($"{Host}/takelogin.php", content, null, null, null, true, false, ct);

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var cookie = string.Join("; ", cookies);
            await CacheService.SetAsync(CookieKey, cookie, TimeSpan.FromDays(Config.Cache.AuthExpiry));
            return cookie;
        }

        return string.Empty;
    }

    protected static IReadOnlyCollection<TorrentDetails> ParseBrowsePage(string html, string host)
    {
        var list = new List<TorrentDetails>();
        var regex = new Regex(@"<tr class=bg>.*?</tr>", RegexOptions.Singleline | RegexOptions.Compiled);
        var matches = regex.Matches(html);
        var now = DateTime.UtcNow;

        foreach (Match match in matches)
        {
            var row = match.Value;

            // Category
            var catMatch = Regex.Match(row, @"onclick=""cat\((\d+)\);""");
            if (!catMatch.Success) continue;
            var catId = catMatch.Groups[1].Value;
            var types = GetTypes(catId);
            if (types == null) continue;

            // Title & URL
            var titleMatch = Regex.Match(row, @"<a href=""/details\.php\?id=(\d+)"" class=""r\d"">([^<]+)</a>");
            if (!titleMatch.Success) continue;
            var id = titleMatch.Groups[1].Value;
            var title = WebUtility.HtmlDecode(titleMatch.Groups[2].Value);
            var url = $"{host}/details.php?id={id}";

            // Size
            var sizeMatch = Regex.Match(row, @"<td class='s'>([\d\.]+) (ГБ|МБ|КБ|ТБ)</td>");
            long size = 0;
            string? sizeName = null;
            if (sizeMatch.Success)
            {
                sizeName = sizeMatch.Groups[0].Value.Replace("<td class='s'>", "").Replace("</td>", "");
                size = ParseSize(sizeMatch.Groups[1].Value, sizeMatch.Groups[2].Value);
            }

            // Seeds/Peers
            var seedsMatch = Regex.Match(row, @"<td class='sl_s'>(\d+)</td>");
            var peersMatch = Regex.Match(row, @"<td class='sl_p'>(\d+)</td>");
            int seeds = 0, peers = 0;
            if (seedsMatch.Success) int.TryParse(seedsMatch.Groups[1].Value, out seeds);
            if (peersMatch.Success) int.TryParse(peersMatch.Groups[1].Value, out peers);

            // Date
            var dateMatch = Regex.Match(row, @"<td class='s'>(\d{2}\.\d{2}\.\d{4}) в (\d{2}:\d{2})</td>");
            DateTime createTime = default;
            if (dateMatch.Success)
            {
                var dateStr = $"{dateMatch.Groups[1].Value} {dateMatch.Groups[2].Value}";
                DateTime.TryParseExact(dateStr, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out createTime);
            }

            // Year from title
            var year = ExtractYear(title);

            list.Add(new TorrentDetails
            {
                TrackerName = "kinozal",
                Types = types,
                Url = url,
                Title = title,
                Sid = seeds,
                Pir = peers,
                Size = size,
                SizeName = sizeName,
                CreateTime = createTime,
                Relased = year,
                UpdateTime = now,
                CheckTime = now
            });
        }

        return list;
    }

    private static string[]? GetTypes(string cat)
    {
        return cat switch
        {
            "8" or "6" or "15" or "17" or "35" or "39" or "13" or "14" or "24" or "11" or "9" or "47" or "18" or "37"
                or "12" or "10" or "7" or "16" => ["movie"],
            "45" or "46" => ["serial"],
            "49" or "50" => ["tvshow"],
            "21" or "22" => ["multfilm", "multserial"],
            "20" => ["anime"],
            _ => null
        };
    }

    private new static long ParseSize(string value, string unit)
    {
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num)) return 0;

        return unit switch
        {
            "ТБ" => (long)(num * 1024 * 1024 * 1024 * 1024),
            "ГБ" => (long)(num * 1024 * 1024 * 1024),
            "МБ" => (long)(num * 1024 * 1024),
            "КБ" => (long)(num * 1024),
            _ => (long)num
        };
    }

    private static int ExtractYear(string title)
    {
        var match = Regex.Match(title, @"\b(19|20)\d{2}\b");
        return match.Success && int.TryParse(match.Value, out var year) ? year : 0;
    }
}