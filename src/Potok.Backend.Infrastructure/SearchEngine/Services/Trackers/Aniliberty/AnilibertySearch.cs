using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.Aniliberty;

public class AnilibertySearch : BaseTrackerSearch
{
    public AnilibertySearch(IOptions<Config> config, HttpService httpService, ICacheService cacheService) : base(config,
        httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Aniliberty;
    public override string TrackerName => "aniliberty";
    public override string Host => "https://aniliberty.top";

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query)
    {
        if (!Config.Aniliberty.EnableSearch)
            return [];

        var releases = await SearchReleasesAsync(query);
        if (releases.Count == 0)
            return [];

        var torrents = new ConcurrentBag<TorrentDetails>();

        await Parallel.ForEachAsync(releases, async (release, _) =>
        {
            var releaseTorrents = await FetchReleaseTorrentsAsync(release.Id);
            if (releaseTorrents.Count == 0)
                return;

            foreach (var t in releaseTorrents)
            {
                var item = releases.FirstOrDefault(r => r.Id == release.Id);
                torrents.Add(Map(release, t, item?.Name, item?.OriginalName));
            }
        });

        return torrents;
    }

    private async Task<List<ReleaseDto>> SearchReleasesAsync(string query)
    {
        var url = $"{Host}/api/v1/app/search/releases?query={Uri.EscapeDataString(query)}&include=id,name,year,alias";
        try
        {
            var json = await HttpService.GetStringAsync(url, new RequestOptions { TimeoutSeconds = 10 });
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var data = JsonDocument.Parse(json);
            if (data.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return data.RootElement
                .EnumerateArray()
                .Select(re => new ReleaseDto
                {
                    Id = re.GetProperty("id").GetInt32(),
                    Year = re.TryGetProperty("year", out var y) ? y.GetInt32() : 0,
                    Alias = re.TryGetProperty("alias", out var al) ? al.GetString() : null,
                    Name = re.TryGetProperty("name", out var nameObj) && nameObj.TryGetProperty("main", out var main)
                        ? main.GetString()
                        : null,
                    OriginalName = re.TryGetProperty("name", out var nameObj2) &&
                                   nameObj2.TryGetProperty("english", out var eng)
                        ? eng.GetString()
                        : null
                })
                .Where(r => r.Id > 0)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<TorrentDto>> FetchReleaseTorrentsAsync(int releaseId)
    {
        var url =
            $"{Host}/api/v1/anime/torrents/release/{releaseId}?include=id,hash,size,type,quality,label,magnet,filename,seeders,leechers,updated_at,created_at,description";
        try
        {
            var json = await HttpService.GetStringAsync(url, new RequestOptions { TimeoutSeconds = 10 });
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return doc.RootElement
                .EnumerateArray()
                .Select(t => new TorrentDto
                {
                    Id = t.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    Hash = t.TryGetProperty("hash", out var h) ? h.GetString() : null,
                    Size = t.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Type = t.TryGetProperty("type", out var tp) && tp.TryGetProperty("value", out var tv)
                        ? tv.GetString()
                        : null,
                    Quality = t.TryGetProperty("quality", out var q) && q.TryGetProperty("value", out var qv)
                        ? qv.GetString()
                        : null,
                    Label = t.TryGetProperty("label", out var lb) ? lb.GetString() : null,
                    Magnet = t.TryGetProperty("magnet", out var m) ? m.GetString() : null,
                    FileName = t.TryGetProperty("filename", out var fn) ? fn.GetString() : null,
                    Seeders = t.TryGetProperty("seeders", out var se) ? se.GetInt32() : 0,
                    Leechers = t.TryGetProperty("leechers", out var le) ? le.GetInt32() : 0,
                    Description = t.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    UpdatedAt = t.TryGetProperty("updated_at", out var upd) && upd.ValueKind == JsonValueKind.String
                        ? upd.GetString()
                        : null,
                    CreatedAt = t.TryGetProperty("created_at", out var cr) && cr.ValueKind == JsonValueKind.String
                        ? cr.GetString()
                        : null
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.Magnet))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private TorrentDetails Map(ReleaseDto release, TorrentDto t, string? name, string? originalName)
    {
        var torrentLabel = t.Label ?? t.FileName ?? "Torrent";
        var title = string.IsNullOrWhiteSpace(name) ? torrentLabel : $"{name} ({torrentLabel})";

        var url = !string.IsNullOrWhiteSpace(release.Alias)
            ? $"{Host}/anime/{release.Alias}#torrent-{t.Id}"
            : $"{Host}#torrent-{t.Id}";

        var create = ParseDate(t.CreatedAt) ?? DateTime.UtcNow;
        var update = ParseDate(t.UpdatedAt) ?? create;

        return new TorrentDetails
        {
            Id = Guid.NewGuid(),
            TrackerName = TrackerName,
            Types = ["anime"],
            Url = url,
            Title = title,
            Sid = t.Seeders,
            Pir = t.Leechers,
            Size = t.Size,
            SizeName = t.Size > 0 ? StringConvert.FormatSize(t.Size) : null,
            CreateTime = create,
            UpdateTime = update,
            CheckTime = DateTime.UtcNow,
            Magnet = t.Magnet,
            Name = title,
            OriginalName = originalName,
            Relased = release.Year,
            Quality = ParseQuality(t.Quality),
            VideoType = t.Type
        };
    }

    private static int ParseQuality(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return StringConvert.ParseQuality(value);
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParse(raw, out var dt))
            return dt.ToUniversalTime();

        return null;
    }

    private sealed record ReleaseDto
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string? OriginalName { get; init; }
        public string? Alias { get; init; }
        public int Relased => Year;
        public int Year { get; init; }
    }

    private sealed record TorrentDto
    {
        public int Id { get; init; }
        public string? Hash { get; init; }
        public long Size { get; init; }
        public string? Type { get; init; }
        public string? Quality { get; init; }
        public string? Label { get; init; }
        public string? Magnet { get; init; }
        public string? FileName { get; init; }
        public int Seeders { get; init; }
        public int Leechers { get; init; }
        public string? Description { get; init; }
        public string? UpdatedAt { get; init; }
        public string? CreatedAt { get; init; }
    }
}