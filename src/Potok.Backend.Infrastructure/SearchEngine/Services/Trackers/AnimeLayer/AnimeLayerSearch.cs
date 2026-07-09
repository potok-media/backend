using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.AnimeLayer;

public class AnimeLayerSearch : BaseAnimeLayer
{
    private readonly ITorrentRepository _torrentRepository;

    public AnimeLayerSearch(
        ICacheService cacheService,
        TrackerHttpClient httpService,
        IOptionsSnapshot<Config> config,
        ITorrentRepository torrentRepository)
        : base(cacheService, httpService, config)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!Config.AnimeLayer.EnableSearch)
            return [];

        var url = $"{Host}/torrents/anime/?q={Uri.EscapeDataString(query)}";
        var html = await Get(url, url, ct);

        if (string.IsNullOrWhiteSpace(html) || !html.Contains("id=\"wrapper\""))
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

        return torrents;
    }

    private IReadOnlyCollection<TorrentDetails> Parse(string html)
    {
        var list = new List<TorrentDetails>();
        var rows = WebUtility.HtmlDecode(html.Replace("&nbsp;", ""))
            .Split("class=\"torrent-item torrent-item-medium panel\"").Skip(1);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;

            string Match(string pattern, int index = 1)
            {
                var res = Regex.Match(row, pattern, RegexOptions.IgnoreCase).Groups[index].Value.Trim();
                return Regex.Replace(res, "[\n\r\t ]+", " ").Trim();
            }

            // Дата создания
            var createTime = DateTime.UtcNow;
            if (Regex.IsMatch(row, "(Добавл|Обновл)[^<]+</span>[0-9]+ [^ ]+ [0-9]{4}"))
            {
                createTime = ParseCreateTime(Match(">(Добавл|Обновл)[^<]+</span>([0-9]+ [^ ]+ [0-9]{4})", 2));
            }
            else
            {
                var date = Match("(Добавл|Обновл)[^<]+</span>([^\n]+) в", 2);
                if (!string.IsNullOrWhiteSpace(date))
                    createTime = ParseCreateTime($"{date} {DateTime.Today.Year}");
            }

            // Данные раздачи
            var gurl = Regex.Match(row, "<a href=\"/(torrent/[a-z0-9]+)/?\">([^<]+)</a>").Groups;
            var urlSuffix = gurl[1].Value;
            var title = gurl[2].Value;

            if (string.IsNullOrWhiteSpace(urlSuffix) || string.IsNullOrWhiteSpace(title)) continue;

            var sidStr = Match("class=\"icon s-icons-upload\"></i>([0-9]+)");
            var pirStr = Match("class=\"icon s-icons-download\"></i>([0-9]+)");

            var sizeMatch = Regex.Match(row,
                @"class=""icon s-icons-download""></i>\d+\s*<span class=""gray"">\s*\|\s*</span>\s*([\d\.,]+\s*[TGMK]B)",
                RegexOptions.IgnoreCase);

            long size = 0;
            string? sizeName = null;

            if (sizeMatch.Success)
            {
                sizeName = sizeMatch.Groups[1].Value.Trim();
                size = ParseSize(sizeName);
            }

            if (Regex.IsMatch(row, "Разрешение: ?</strong>1920x1080"))
                title += " [1080p]";
            else if (Regex.IsMatch(row, "Разрешение: ?</strong>1280x720"))
                title += " [720p]";

            var url = $"{Host}/{urlSuffix}/";

            // name / originalname
            string? name = null, originalname = null;

            // Shaman king (2021) / Король-шаман [ТВ] (1-7)
            var g = Regex.Match(title, "([^/\\[\\(]+)\\([0-9]{4}\\)[^/]+/([^/\\[\\(]+)").Groups;
            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
            {
                name = g[2].Value.Trim();
                originalname = g[1].Value.Trim();
            }
            else
            {
                // Shadows House / Дом теней (1—6)
                g = Regex.Match(title, "^([^/\\[\\(]+)/([^/\\[\\(]+)").Groups;
                if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                {
                    name = g[2].Value.Trim();
                    originalname = g[1].Value.Trim();
                }
            }

            // Год выхода
            var relased = 0;
            if (int.TryParse(Match("Год выхода: ?</strong>([0-9]{4})"), out var r))
                relased = r;

            if (string.IsNullOrWhiteSpace(name))
                name = Regex.Split(title, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();

            if (!string.IsNullOrWhiteSpace(name))
            {
                int.TryParse(sidStr, out var sid);
                int.TryParse(pirStr, out var pir);

                list.Add(new TorrentDetails
                {
                    TrackerName = TrackerName,
                    Types = ["anime"],
                    Url = url,
                    Title = title,
                    Sid = sid,
                    Pir = pir,
                    Size = size,
                    SizeName = sizeName,
                    CreateTime = createTime,
                    Name = name,
                    OriginalName = originalname,
                    Relased = relased
                });
            }
        }

        return list;
    }

    private static long ParseSize(string sizeStr)
    {
        var match = Regex.Match(sizeStr, @"([\d\.,]+)\s*([TGMK]B)", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        if (!double.TryParse(match.Groups[1].Value.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture,
                out var value))
            return 0;

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "MB" => 1024d * 1024d,
            "KB" => 1024d,
            _ => 1d
        };

        return (long)(value * multiplier);
    }

    private static DateTime ParseCreateTime(string dateStr)
    {
        if (DateTime.TryParseExact(dateStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var date))
            return date;

        dateStr = dateStr.ToLower()
            .Replace("января", ".01.")
            .Replace("февраля", ".02.")
            .Replace("марта", ".03.")
            .Replace("апреля", ".04.")
            .Replace("мая", ".05.")
            .Replace("июня", ".06.")
            .Replace("июля", ".07.")
            .Replace("августа", ".08.")
            .Replace("сентября", ".09.")
            .Replace("октября", ".10.")
            .Replace("ноября", ".11.")
            .Replace("декабря", ".12.");

        if (DateTime.TryParse(dateStr, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out date))
            return date;

        return DateTime.UtcNow;
    }
}