using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTor;

public class BaseRuTor : BaseTrackerSearch, ITrackerCatalogEnricher
{
    private readonly HtmlParser _parser = new();

    protected BaseRuTor(IOptions<Config> config, HttpService httpService, ICacheService cacheService) : base(config,
        httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Rutor;
    public override string TrackerName => "rutor";
    public override string Host => "http://rutor.info/";
    protected string SearchUrl => $"{Host}search/0/0/100/2/";

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        var html = await HttpService.GetStringAsync(torrent.Url, new RequestOptions { Referer = torrent.Url });
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var document = await _parser.ParseDocumentAsync(html);

        var detailsTable = document.QuerySelector("table#details");
        if (detailsTable != null) ParseDetailsTable(detailsTable, torrent);

        if (torrent.Types?.Length == 0)
            return false;

        return !string.IsNullOrWhiteSpace(torrent.Name) || !string.IsNullOrWhiteSpace(torrent.Magnet);
    }

    protected IReadOnlyCollection<TorrentDetails> Parse(string html)
    {
        var list = new List<TorrentDetails>();
        var document = _parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("#index tr.gai, #index tr.tum");

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 4) continue;

            var dateCell = cells[0];
            var titleCell = cells[1];
            var sizeCell = cells.Length > 3 ? cells[^2] : null;
            var seedsPeersCell = cells.Length > 3 ? cells[^1] : null;

            if (cells.Length == 4 && cells[1].GetAttribute("colspan") == "2")
            {
            }
            else if (cells.Length == 5)
            {
                titleCell = cells[1];
                sizeCell = cells[3];
                seedsPeersCell = cells[4];
            }

            var magnetLink = titleCell.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(magnetLink)) continue;

            var titleLink = titleCell.QuerySelector("a[href^='/torrent/']");
            if (titleLink == null) continue;

            var title = titleLink.TextContent.Trim();
            var url = titleLink.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var match = Regex.Match(url, @"^/torrent/\d+");
                if (match.Success)
                {
                    url = Host.TrimEnd('/') + match.Value;
                }
                else if (!url.StartsWith("http"))
                {
                    url = Host.TrimEnd('/') + url;
                }
            }

            long size = 0;
            string? sizeName = null;
            if (sizeCell != null)
            {
                var sizeText = sizeCell.TextContent.Trim();
                // Format: 8.18 GB
                var sizeParts = sizeText.Split(new[] { ' ', '&', '\u00A0' }, StringSplitOptions.RemoveEmptyEntries);
                if (sizeParts.Length >= 2)
                {
                    size = ParseSize(sizeParts[0], sizeParts[1]);
                    sizeName = string.Concat((object?)sizeParts[0], (object?)sizeParts[1]);
                }
            }

            var seeds = 0;
            var peers = 0;
            if (seedsPeersCell != null)
            {
                var seedsElement = seedsPeersCell.QuerySelector("span.green");
                var peersElement = seedsPeersCell.QuerySelector("span.red");

                if (seedsElement != null)
                {
                    var seedsText = seedsElement.TextContent.Trim();
                    var seedsMatch = Regex.Match(seedsText, @"\d+");
                    if (seedsMatch.Success)
                        int.TryParse(seedsMatch.Value, out seeds);
                }

                if (peersElement != null)
                {
                    var peersText = peersElement.TextContent.Trim();
                    var peersMatch = Regex.Match(peersText, @"\d+");
                    if (peersMatch.Success)
                        int.TryParse(peersMatch.Value, out peers);
                }
            }

            var date = DateTime.UtcNow;
            if (dateCell != null)
            {
                var dateText = dateCell.TextContent.Trim();
                var dateParts = dateText.Split([' ', '&', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
                if (dateParts.Length >= 3) date = ParseDate(dateParts[0], dateParts[1], dateParts[2]);
            }

            list.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Title = title,
                Url = url ?? string.Empty,
                Magnet = WebUtility.HtmlDecode(magnetLink),
                Size = size,
                SizeName = sizeName,
                Sid = seeds,
                Pir = peers,
                CreateTime = date,
                UpdateTime = DateTime.UtcNow,
                CheckTime = DateTime.Now,
                Types = []
            });
        }

        return list;
    }

    private void ParseDetailsTable(IElement detailsTable, TorrentDetails torrent)
    {
        var nameElement = detailsTable.QuerySelectorAll("b").FirstOrDefault(e => e.TextContent.Contains("Название:"));
        if (nameElement?.NextSibling != null)
            torrent.Name = nameElement.NextSibling.TextContent.Trim();

        var originalNameElement = detailsTable.QuerySelectorAll("b")
            .FirstOrDefault(e => e.TextContent.Contains("Оригинальное название:"));
        if (originalNameElement?.NextSibling != null)
            torrent.OriginalName = originalNameElement.NextSibling.TextContent.Trim();

        var yearElement = detailsTable.QuerySelectorAll("b").FirstOrDefault(e => e.TextContent.Contains("Год выхода:"));
        if (yearElement?.NextSibling != null && int.TryParse(yearElement.NextSibling.TextContent.Trim(), out var year))
            torrent.Relased = year;

        var categoryLink = detailsTable.QuerySelectorAll("tr")
            .FirstOrDefault(tr => tr.QuerySelector("td.header")?.TextContent.Contains("Категория") == true)
            ?.QuerySelector("a");

        if (categoryLink != null)
        {
            var href = categoryLink.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(href))
            {
                var category = href.Trim('/').Split('/').LastOrDefault();
                if (category != null)
                    torrent.Types = MapCategory(category);
            }
        }

        var qualityElement =
            detailsTable.QuerySelectorAll("b").FirstOrDefault(e => e.TextContent.Contains("Качество:"));
        if (qualityElement?.NextSibling != null)
            torrent.Quality = StringConvert.ParseQuality(qualityElement.NextSibling.TextContent.Trim());

        var formatElement = detailsTable.QuerySelectorAll("b").FirstOrDefault(e => e.TextContent.Contains("Формат:"));
        if (formatElement?.NextSibling != null)
            torrent.VideoType = formatElement.NextSibling.TextContent.Trim();

        var translationElement =
            detailsTable.QuerySelectorAll("b").FirstOrDefault(e => e.TextContent.Contains("Перевод:"));
        if (translationElement?.NextSibling != null)
        {
            var translationText = translationElement.NextSibling.TextContent.Trim();
            if (!string.IsNullOrWhiteSpace(translationText))
                torrent.Voices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { translationText };
        }
    }

    private static string[] MapCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return [];

        if (category.Contains("seriali", StringComparison.OrdinalIgnoreCase))
            return ["serial"];
        if (category.Contains("anime", StringComparison.OrdinalIgnoreCase))
            return ["anime"];
        if (category.Contains("kino", StringComparison.OrdinalIgnoreCase))
            return ["movie"];
        if (category.Contains("nashe_kino", StringComparison.OrdinalIgnoreCase))
            return ["movie"];
        if (category.Contains("nashi_seriali", StringComparison.OrdinalIgnoreCase))
            return ["serial"];
        if (category.Contains("tv", StringComparison.OrdinalIgnoreCase))
            return ["tvshow"];
        if (category.Contains("multiki", StringComparison.OrdinalIgnoreCase))
            return ["multfilm"];

        return [];
    }
}