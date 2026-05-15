using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;

public class BaseRuTracker : BaseTrackerSearch, ITrackerCatalogEnricher
{
    private const string CookieKey = "rutracker:cookie";

    private readonly HtmlParser _parser = new();

    protected static readonly IReadOnlyDictionary<string, CategoryInfo> CategoryMap = BuildCategoryMap();

    private readonly IOptions<Config> _config;
    
    protected BaseRuTracker(IOptions<Config> config, HttpService httpService, ICacheService cacheService) : base(config,
        httpService, cacheService)
    {
        _config = config;
    }

    public override TrackerType Tracker => TrackerType.Rutracker;
    public override string TrackerName => "rutracker";
    public override string Host => "https://rutracker.org/";

    private string LoginUrl => Host + "forum/login.php";

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        var details = await FetchTopicDetailsAsync(torrent.Url);
        if (string.IsNullOrWhiteSpace(details.Magnet))
            return false;

        torrent.Magnet = details.Magnet;

        return true;
    }

    protected async Task<IReadOnlyCollection<TorrentDetails>> FetchForumPageAsync(
        string url,
        string categoryId,
        DateTime now,
        int timeoutSeconds = 10,
        int maxResponseSize = 10_000_000,
        bool useProxy = false)
    {
        var html = await Get(
            url,
            RuEncoding,
            url,
            timeoutSeconds,
            maxResponseSize,
            useProxy: useProxy);

        if (string.IsNullOrWhiteSpace(html))
            return [];

        return ParseForumPage(html, categoryId, Host, now);
    }

    protected async Task<string> Get(
        string url,
        Encoding? encoding = null,
        string? referer = null,
        int timeoutSeconds = 15,
        int maxResponseSize = 10_000_000,
        List<(string name, string val)>? addHeaders = null,
        bool useProxy = false)
    {
        if (!CacheService.TryGetValue(CookieKey, out string? cookie))
            cookie = await Authorize();

        var html = await HttpService.GetStringAsync(
            url,
            new RequestOptions
            {
                Encoding = encoding ?? Encoding.UTF8,
                Cookie = cookie,
                Referer = referer,
                TimeoutSeconds = timeoutSeconds,
                MaxResponseSizeBytes = maxResponseSize,
                Headers = addHeaders?.ToDictionary(x => x.name, x => x.val)
            });

        if (string.IsNullOrWhiteSpace(html) || !html.Contains("id=\"logged-in-username\""))
        {
            cookie = await Authorize(true);
            html = await HttpService.GetStringAsync(
                url,
                new RequestOptions
                {
                    Encoding = encoding ?? Encoding.UTF8,
                    Cookie = cookie,
                    Referer = referer,
                    TimeoutSeconds = timeoutSeconds,
                    MaxResponseSizeBytes = maxResponseSize,
                    Headers = addHeaders?.ToDictionary(x => x.name, x => x.val)
                });
        }

        return html;
    }

    private async Task<string> Authorize(bool reAuth = false)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "login_username", Config.RuTracker.Authorization.Login },
            { "login_password", Config.RuTracker.Authorization.Password },
            { "login", "Login" }
        };

        var formEncoded = string.Join("&",
            pairs.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

        var response = await HttpService.PostResponseAsync(
            LoginUrl,
            new StringContent(formEncoded, Encoding.Default, "application/x-www-form-urlencoded"),
            new RequestOptions { AllowAutoRedirect = false });

        if (response.StatusCode is not HttpStatusCode.Found)
        {
            if (reAuth)
                return string.Empty;

            return await Authorize(true);
        }

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var v) ? v : [];
        var cookie = string.Join("; ", cookies);

        await CacheService.SetAsync(CookieKey, cookie, TimeSpan.FromDays(Config.Cache.AuthExpiry));

        return cookie;
    }

    protected IReadOnlyCollection<TorrentDetails> ParseForumPage(
        string html,
        string categoryId,
        string host,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var cleaned = ReplaceBadNames(html);
        var results = new List<TorrentDetails>();
        var baseForumUri = new Uri(new Uri(host), "forum/");

        var document = _parser.ParseDocument(cleaned);
        var rows = document.QuerySelectorAll("tr.hl-tr");

        foreach (var row in rows)
        {
            var linkElement = row.QuerySelector("a.tLink");
            if (linkElement == null)
                continue;

            var href = NormalizeHref(linkElement.GetAttribute("href"));
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var titleRaw = linkElement.TextContent;
            var title = NormalizeText(titleRaw);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var url = new Uri(baseForumUri, href).ToString();

            var sizeName = string.Empty;
            var sizeBytes = 0L;
            
            var sizeElement = row.QuerySelector("a.small.tr-dl");
            if (sizeElement != null)
            {
                 var sizeText = sizeElement.TextContent.Trim();
                 if (TryParseSize(sizeText, out var parsedSizeName, out var parsedSizeBytes))
                 {
                     sizeName = parsedSizeName;
                     sizeBytes = parsedSizeBytes;
                 }
            }
            else
            {
                var sizeTd = row.QuerySelector("td.tor-size");
                if (sizeTd != null)
                {
                    var tsText = sizeTd.GetAttribute("data-ts_text");
                    if (long.TryParse(tsText, out var bytes))
                    {
                        sizeBytes = bytes;
                        sizeName = FormatSize(bytes);
                    }
                }
            }

            var publishDate = TryParsePublishDate(row) ?? now;

            var rowCategoryId = categoryId;
            if (!TryGetCategory(rowCategoryId, out var category))
            {
                rowCategoryId = ExtractCategoryId(row);
                if (!TryGetCategory(rowCategoryId, out category))
                    continue;
            }

            var (name, originalName, relased) = ParseTitle(title, category);
            if (category.Parser == CategoryParser.Generic && SeasonMarkerRegex.IsMatch(title))
                continue;

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Regex.Split(title, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
            }

            var seedElement = row.QuerySelector("b.seedmed");
            var leechElement = row.QuerySelector("td.leechmed");

            int sid = 0;
            if (seedElement != null)
                sid = ParseInt(seedElement.TextContent);
            else
            {
                 var seedTd = row.QuerySelector("td.seedmed");
                 if (seedTd != null) sid = ParseInt(seedTd.TextContent);
            }

            int pir = 0;
            if (leechElement != null)
                pir = ParseInt(leechElement.TextContent);

            results.Add(new TorrentDetails
            {
                TrackerName = "rutracker",
                Types = category.Types,
                Url = url,
                Title = title,
                Sid = sid,
                Pir = pir,
                SizeName = string.IsNullOrWhiteSpace(sizeName) ? null : sizeName,
                CreateTime = publishDate,
                UpdateTime = now,
                CheckTime = now,
                Name = name,
                OriginalName = originalName,
                Relased = relased,
                Size = sizeBytes
            });
        }

        return results;
    }

    private static IReadOnlyDictionary<string, CategoryInfo> BuildCategoryMap()
    {
        var map = new Dictionary<string, CategoryInfo>(StringComparer.OrdinalIgnoreCase);

        Add(map, CategoryParser.Movie, ["movie"],
            "549", "22", "1666", "941", "1950", "2090", "2221", "2091", "2092", "2093", "2200",
            "2540", "934", "505", "124", "1457", "2199", "313", "312", "1247", "2201", "2339", "140",
            "252", "718", "2198");

        Add(map, CategoryParser.Movie, ["multfilm"], "2343", "930", "2365", "208", "539", "209", "1213");
        Add(map, CategoryParser.Serial, ["multserial"], "921", "815", "1460");

        Add(map, CategoryParser.Serial, ["serial"],
            "842", "235", "242", "819", "1531", "721", "1102", "1120", "1214", "489", "387", "9", "81",
            "119", "1803", "266", "193", "1690", "1459", "825", "1248", "1288", "325", "534", "694",
            "704", "915", "1939");

        Add(map, CategoryParser.Generic, ["anime"], "1105", "1106", "2491", "1389");
        Add(map, CategoryParser.Movie, ["documovie"], "709", "2109");
        Add(map, CategoryParser.Generic, ["docuserial", "documovie"],
            "46", "671", "2177", "2538", "251", "98", "97", "851", "2178", "821", "2076", "56", "2123",
            "876", "2139", "1467", "1469", "249", "552", "500", "2112", "1327", "1468", "2168", "2160",
            "314", "1281", "2110", "979", "2169", "2164", "2166", "2163");
        Add(map, CategoryParser.Generic, ["tvshow"], "24", "1959", "939", "1481", "113", "115", "882", "1482",
            "393", "2537", "532", "827");
        Add(map, CategoryParser.Generic, ["sport"],
            "2103", "2522", "2485", "2486", "2479", "2089", "1794", "845", "2312", "343", "2111",
            "1527", "2069", "1323", "2009", "2000", "2010", "2006", "2007", "2005", "259", "2004",
            "1999", "2001", "2002", "283", "1997", "2003", "1608", "1609", "2294", "1229", "1693",
            "2532", "136", "592", "2533", "1952", "1621", "2075", "1668", "1613", "1614", "1623",
            "1615", "1630", "2425", "2514", "1616", "2014", "1442", "1491", "1987", "1617", "1620",
            "1998", "1343", "751", "1697", "255", "260", "261", "256", "1986", "660", "1551", "626",
            "262", "1326", "978", "1287", "1188", "1667", "1675", "257", "875", "263", "2073", "550",
            "2124", "1470", "528", "486", "854", "2079", "1336", "2171", "1339", "2455", "1434",
            "2350", "1472", "2068", "2016");

        return map;
    }

    private static void Add(Dictionary<string, CategoryInfo> map, CategoryParser parser, string[] types,
        params string[] categories)
    {
        foreach (var category in categories)
            map[category] = new CategoryInfo(types, parser);
    }

    private static bool TryGetCategory(string categoryId, out CategoryInfo category)
    {
        return CategoryMap.TryGetValue(categoryId, out category!);
    }

    protected static (string? name, string? originalName, int relased) ParseTitle(string title, CategoryInfo category)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null, 0);

        return category.Parser switch
        {
            CategoryParser.Movie => ParseMovie(title),
            CategoryParser.Serial => ParseSerial(title),
            CategoryParser.Generic => ParseGeneric(title),
            _ => (title.Trim(), null, ExtractYear(title))
        };
    }

    private static (string? name, string? originalName, int relased) ParseMovie(string title)
    {
        var g = Regex.Match(title, "^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeMovie(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title, "^([^/\\(\\[]+) / ([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeMovie(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title, "^([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2))
            return NormalizeMovie(g[1].Value, null, g[2].Value);

        return (title.Trim(), null, ExtractYear(title));
    }

    private static (string? name, string? originalName, int relased) ParseSerial(string title)
    {
        var seasonPattern = Regex.Escape("Сезон");
        var g = Regex.Match(title,
            $"^([^/\\(\\[]+) / [^/\\(\\[]+ / [^/\\(\\[]+ / ([^/\\(\\[]+) / {seasonPattern}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) / {seasonPattern}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / ([^/\\(\\[]+) / {seasonPattern}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / {seasonPattern}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2))
            return NormalizeSerial(g[1].Value, null, g[2].Value);

        g = Regex.Match(title,
            "^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            "^([^/\\(\\[]+) / ([^/\\(\\[]+) / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        return (title.Trim(), null, ExtractYear(title));
    }

    private static (string? name, string? originalName, int relased) ParseGeneric(string title)
    {
        var name = Regex.Match(title, "^([^/\\(\\[]+) ").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(name))
            return (title.Trim(), null, ExtractYear(title));

        if (Regex.IsMatch(name, "(Сезон|Серии)", RegexOptions.IgnoreCase))
            return (null, null, 0);

        var relased = 0;
        var yearMatch = Regex.Match(title, " \\[([0-9]{4})(,|-) ");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsed))
            relased = parsed;

        return (name.Trim(), null, relased);
    }

    private static (string? name, string? originalName, int relased) NormalizeMovie(string? name, string? original,
        string year)
    {
        var relased = ParseYear(year);
        name = name?.Replace("в 3Д", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        original = original?.Replace(" in 3D", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" 3D", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return (name, original, relased);
    }

    private static (string? name, string? originalName, int relased) NormalizeSerial(string? name, string? original,
        string year)
    {
        var relased = ParseYear(year);
        return (name?.Trim(), original?.Trim(), relased);
    }

    private static bool IsValidGroups(GroupCollection groups, params int[] indices)
    {
        return indices.All(i => groups.Count > i && !string.IsNullOrWhiteSpace(groups[i].Value));
    }

    private static int ParseYear(string yearRaw)
    {
        return int.TryParse(yearRaw, out var year) ? year : 0;
    }

    private static int ExtractYear(string title)
    {
        var match = YearRegex.Match(title);
        return match.Success && int.TryParse(match.Value, out var year) ? year : 0;
    }

    private static string ReplaceBadNames(string html)
    {
        return html
            .Replace("Ванда/Вижн ", "ВандаВижн ")
            .Replace("Ё", "Е")
            .Replace("ё", "е")
            .Replace("щ", "ш");
    }

    private static int ParseInt(string raw)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return WhitespaceRegex.Replace(text, " ").Trim();
    }

    private async
        Task<(string? Magnet, DateTime CreateTime, string? Title, int Sid, int Pir, long Size, string? SizeName)>
        FetchTopicDetailsAsync(
            string url, bool force = false)
    {
        try
        {
            var html = await Get(
                url,
                RuEncoding,
                url,
                7);

            if (string.IsNullOrWhiteSpace(html))
                return (null, default, null, 0, 0, 0, null);

            var document = await _parser.ParseDocumentAsync(html);

            var magnetElement = document.QuerySelector("a.magnet-link");
            var magnet = magnetElement?.GetAttribute("href");

            var dateElement = document.QuerySelector("a.p-link.small");
            
            var dateRaw = string.Empty;
            if (dateElement != null && dateElement.GetAttribute("href")?.Contains("viewtopic.php") == true)
            {
                dateRaw = dateElement.TextContent;
            }
            else
            {
                var links = document.QuerySelectorAll("a.p-link.small");
                foreach (var link in links)
                {
                    if (link.GetAttribute("href")?.Contains("viewtopic.php") == true)
                    {
                        dateRaw = link.TextContent;
                        break;
                    }
                }
            }
            
            var createTime = ParseTopicDate(dateRaw);

            string? title = null;
            int sid = 0;
            int pir = 0;
            long size = 0;
            string? sizeName = null;

            if (force)
            {
                var titleElement = document.QuerySelector("a#topic-title");
                if (titleElement != null)
                    title = NormalizeText(titleElement.TextContent);

                var sidElement = document.QuerySelector("span.seed b");
                if (sidElement != null) sid = ParseInt(sidElement.TextContent);

                var pirElement = document.QuerySelector("span.leech b");
                if (pirElement != null) pir = ParseInt(pirElement.TextContent);

                var bTags = document.QuerySelectorAll("b");
                foreach (var b in bTags)
                {
                    var text = b.TextContent.Trim();
                    var match = Regex.Match(text, @"^([\d\.]+)\s+(GB|MB|KB|TB|ГБ|МБ|КБ|ТБ)$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var prev = b.PreviousSibling;
                        if (prev != null && prev.TextContent.Contains("Размер"))
                        {
                             var val = match.Groups[1].Value;
                             var unit = match.Groups[2].Value;
                             sizeName = $"{val} {unit}";
                             size = ParseSize(val, unit);
                             break;
                        }
                    }
                }
            }

            return (magnet, createTime, title, sid, pir, size, sizeName);
        }
        catch (Exception)
        {
            return (null, default, null, 0, 0, 0, null);
        }
    }

    private new static long ParseSize(string value, string unit)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture,
                out var num)) return 0;

        var multiplier = unit.ToUpperInvariant() switch
        {
            "TB" or "ТБ" => 1024d * 1024d * 1024d * 1024d,
            "GB" or "ГБ" => 1024d * 1024d * 1024d,
            "MB" or "МБ" => 1024d * 1024d,
            "KB" or "КБ" => 1024d,
            _ => 1d
        };

        return (long)Math.Round(num * multiplier);
    }

    protected static string BuildCategoryUrl(string host, string categoryId, int page)
    {
        var baseUrl = $"{host.TrimEnd('/')}/forum/tracker.php?f[]={categoryId}&o=10&s=2";
        return page <= 0 ? baseUrl : $"{baseUrl}&start={page * 50}";
    }

    protected static string BuildQueryUrl(string host, string query, int page)
    {
        var baseUrl = $"{host.TrimEnd('/')}/forum/tracker.php?nm={Uri.EscapeDataString(query)}&o=10&s=2";
        return page <= 0 ? baseUrl : $"{baseUrl}&start={page * 50}";
    }

    protected static int GetMaxPages(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return 0;

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        
        var bTags = document.QuerySelectorAll("b");
        foreach (var b in bTags)
        {
            if (int.TryParse(b.TextContent, out var pages))
            {
                var prev = b.PreviousSibling;
                if (prev != null && prev.TextContent.Contains("из"))
                {
                    return pages;
                }
            }
        }

        return 0;
    }

    private static bool TryParseSize(string sizeText, out string sizeName, out long sizeBytes)
    {
        sizeName = string.Empty;
        sizeBytes = 0L;

        var match = Regex.Match(sizeText, @"(?<value>\d+(?:[\.,]\d+)?)\s*(?<unit>TB|GB|MB|KB|ТБ|ГБ|МБ|КБ)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var valueRaw = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(valueRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return false;

        var unitRaw = match.Groups["unit"].Value.ToUpperInvariant();
        var unit = unitRaw switch
        {
            "ТБ" => "TB",
            "ГБ" => "GB",
            "МБ" => "MB",
            "КБ" => "KB",
            _ => unitRaw
        };

        var multiplier = unit switch
        {
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "MB" => 1024d * 1024d,
            "KB" => 1024d,
            _ => 1d
        };

        sizeBytes = (long)Math.Round(value * multiplier);
        sizeName = FormatSize(sizeBytes);
        return true;
    }

    private static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        double value = bytes;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static DateTime? TryParsePublishDate(IElement row)
    {
        var elements = row.QuerySelectorAll("[data-ts_text]");
        foreach (var element in elements)
        {
            if (element.ClassList.Contains("tor-size")) continue;

            var tsRaw = element.GetAttribute("data-ts_text");
            if (!long.TryParse(tsRaw, out var ts)) continue;

            // 2000 year = 946684800
            // 2100 year = 4102444800
            if (ts > 946684800L && ts < 4102444800L)
            {
                return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
            }
        }

        return null;
    }

    private static string NormalizeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return string.Empty;

        href = WebUtility.HtmlDecode(href).Trim();
        if (href.StartsWith("./", StringComparison.Ordinal))
            href = href[2..];

        if (href.StartsWith("viewtopic.php", StringComparison.OrdinalIgnoreCase))
            return href;

        if (href.StartsWith("/forum/", StringComparison.OrdinalIgnoreCase))
            return href["/forum/".Length..];

        if (href.StartsWith("forum/", StringComparison.OrdinalIgnoreCase))
            return href["forum/".Length..];

        return href;
    }

    private static string ExtractCategoryId(IElement row)
    {
        var links = row.QuerySelectorAll("a");
        foreach (var link in links)
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            
            var match = Regex.Match(href, @"tracker\.php\?f(?:%5B%5D|\[\])?=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;
                
            match = Regex.Match(href, @"viewforum\.php\?f=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;
        }

        return string.Empty;
    }

    private static DateTime ParseTopicDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        var normalized = NormalizeDate(raw.Replace("-", " "));
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        if (DateTime.TryParseExact(
                normalized,
                "dd.MM.yy HH:mm",
                CultureInfo.GetCultureInfo("ru-RU"),
                DateTimeStyles.None,
                out var parsed))
            return parsed;

        return DateTime.TryParse(
            normalized,
            CultureInfo.GetCultureInfo("ru-RU"),
            DateTimeStyles.None,
            out parsed)
            ? parsed
            : default;
    }

    private static string NormalizeDate(string value)
    {
        value = ReplaceMonth(value, "янв", ".01.");
        value = ReplaceMonth(value, "февр?", ".02.");
        value = ReplaceMonth(value, "март?", ".03.");
        value = ReplaceMonth(value, "апр", ".04.");
        value = ReplaceMonth(value, "май", ".05.");
        value = ReplaceMonth(value, "июнь?", ".06.");
        value = ReplaceMonth(value, "июль?", ".07.");
        value = ReplaceMonth(value, "авг", ".08.");
        value = ReplaceMonth(value, "сент?", ".09.");
        value = ReplaceMonth(value, "окт", ".10.");
        value = ReplaceMonth(value, "нояб?", ".11.");
        value = ReplaceMonth(value, "дек", ".12.");

        value = ReplaceMonth(value, "январ(ь|я)?", ".01.");
        value = ReplaceMonth(value, "феврал(ь|я)?", ".02.");
        value = ReplaceMonth(value, "марта?", ".03.");
        value = ReplaceMonth(value, "апрел(ь|я)?", ".04.");
        value = ReplaceMonth(value, "май?я?", ".05.");
        value = ReplaceMonth(value, "июн(ь|я)?", ".06.");
        value = ReplaceMonth(value, "июл(ь|я)?", ".07.");
        value = ReplaceMonth(value, "августа?", ".08.");
        value = ReplaceMonth(value, "сентябр(ь|я)?", ".09.");
        value = ReplaceMonth(value, "октябр(ь|я)?", ".10.");
        value = ReplaceMonth(value, "ноябр(ь|я)?", ".11.");
        value = ReplaceMonth(value, "декабр(ь|я)?", ".12.");

        value = ReplaceMonth(value, "Jan", ".01.");
        value = ReplaceMonth(value, "Feb", ".02.");
        value = ReplaceMonth(value, "Mar", ".03.");
        value = ReplaceMonth(value, "Apr", ".04.");
        value = ReplaceMonth(value, "May", ".05.");
        value = ReplaceMonth(value, "Jun", ".06.");
        value = ReplaceMonth(value, "Jul", ".07.");
        value = ReplaceMonth(value, "Aug", ".08.");
        value = ReplaceMonth(value, "Sep", ".09.");
        value = ReplaceMonth(value, "Oct", ".10.");
        value = ReplaceMonth(value, "Nov", ".11.");
        value = ReplaceMonth(value, "Dec", ".12.");

        if (Regex.IsMatch(value, "^[0-9]\\.", RegexOptions.IgnoreCase))
            value = $"0{value}";

        return value;
    }

    private static string ReplaceMonth(string value, string monthPattern, string replacement)
    {
        return Regex.Replace(value, $"\\s{monthPattern}\\.?(\\s|$)", $"{replacement} ", RegexOptions.IgnoreCase);
    }

    #region Category map

    protected enum CategoryParser
    {
        Default,
        Movie,
        Serial,
        Generic
    }

    protected sealed record CategoryInfo(string[] Types, CategoryParser Parser);

    #endregion

    #region Regex helpers

    private static readonly Regex WhitespaceRegex =
        new(@"[\n\r\t ]+", RegexOptions.Compiled);

    private static readonly Regex SeasonMarkerRegex =
        new("(Сезон|Серии)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex =
        new("(?<!\\d)(19\\d{2}|20\\d{2})(?!\\d)", RegexOptions.Compiled);

    #endregion
}