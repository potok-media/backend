using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.MegaPeer;

public class BaseMegaPeer : BaseTrackerSearch, ITrackerCatalogEnricher
{
    private readonly HtmlParser _parser = new();

    public BaseMegaPeer(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService) : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Megapeer;
    public override string TrackerName => "megapeer";
    public override string Host => "https://megapeer.vip/";

    public string SearchUrl => $"{Host}browse.php";
    
    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        var html = await HttpService.GetStringAsync(torrent.Url, referer: torrent.Url, ct: ct);
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var document = await _parser.ParseDocumentAsync(html);

        var magnetLink = document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
        if (!string.IsNullOrWhiteSpace(magnetLink))
        {
            torrent.Magnet = WebUtility.HtmlDecode(magnetLink);
        }

        var categoryLink = document.QuerySelector("a[href^='/cat/']");
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

        if (torrent.Types?.Length == 0)
            return false;

        return !string.IsNullOrWhiteSpace(torrent.Magnet);
    }

    private static string[] MapCategory(string category)
    {
        return category switch
        {
            "80" => ["movie"],
            "79" => ["movie"],
            "5" => ["serial"],
            "6" => ["serial"],
            "55" => ["documovie", "docuserial"],
            "76" => ["multfilm", "multserial", "anime"],
            _ => []
        };
    }

    protected IReadOnlyCollection<TorrentDetails> Parse(string html)
    {
        var list = new List<TorrentDetails>();
        var document = _parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("tr.table_fon");

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 4) continue;

            var dateCell = cells[0];
            IElement titleCell;
            IElement sizeCell;
            IElement seedsPeersCell;

            if (cells.Length == 5)
            {
                titleCell = cells[1];
                sizeCell = cells[3];
                seedsPeersCell = cells[4];
            }
            else
            {
                titleCell = cells[1];
                sizeCell = cells[2];
                seedsPeersCell = cells[3];
            }

            var titleLink = titleCell.QuerySelector("a.url");
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
                var sizeParts = sizeText.Split(new[] { ' ', '&', '\u00A0' }, StringSplitOptions.RemoveEmptyEntries);
                if (sizeParts.Length >= 2)
                {
                    size = ParseSize(sizeParts[0], sizeParts[1]);
                    sizeName = string.Concat(sizeParts[0], sizeParts[1]);
                }
            }

            var seeds = 0;
            var peers = 0;
            if (seedsPeersCell != null)
            {
                var seedsElement = seedsPeersCell.QuerySelector("font[color='#008000']");
                var peersElement = seedsPeersCell.QuerySelector("font[color='#8b0000']");

                if (seedsElement != null)
                {
                    var seedsText = seedsElement.TextContent.Trim();
                    int.TryParse(seedsText, out seeds);
                }

                if (peersElement != null)
                {
                    var peersText = peersElement.TextContent.Trim();
                    int.TryParse(peersText, out peers);
                }
            }

            var date = DateTime.UtcNow;
            if (dateCell != null)
            {
                var dateText = dateCell.TextContent.Trim();
                var dateParts = dateText.Split(new[] { ' ', '&', '\u00A0' }, StringSplitOptions.RemoveEmptyEntries);
                if (dateParts.Length >= 3) date = ParseDate(dateParts[0], dateParts[1], dateParts[2]);
            }

            list.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Title = title,
                Url = url ?? string.Empty,
                Size = size,
                SizeName = sizeName,
                Sid = seeds,
                Pir = peers,
                CreateTime = date,
                UpdateTime = DateTime.UtcNow,
                CheckTime = DateTime.Now
            });
        }

        return list;
    }
}