using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.SearchEngine.Controllers;

[ApiController]
[Route("api/v1/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ISeasonOverrideRepository _overrides;

    public TorrentsController(ISearchService searchService, ISeasonOverrideRepository overrides)
    {
        _searchService = searchService;
        _overrides = overrides;
    }

    [HttpPost("search")]
    public async Task<ActionResult<TorrentSearchResponse>> Search([FromBody] TorrentSearchRequest request)
    {
        var internalRequest = new TorrentSearchQuery
        {
            TmdbId = request.Id,
            Query = request.Query,
            Title = request.Title ?? request.Query,
            TitleOriginal = request.OriginalTitle ?? "",
            Year = int.TryParse(request.Year, out var y) ? y : 0,
            IsSerial = request.MediaType == "tv" ? 2 : 1,
            ForceSearch = request.ForceSearch ?? false
        };

        var results = await _searchService.SearchTorrentsAsync(internalRequest, HttpContext.RequestAborted);
        var sharedResults = results.Select(ToSearchResult).ToList();
        await AttachOverridesAsync(sharedResults);
        return Ok(new TorrentSearchResponse(sharedResults));
    }

    private static TorrentSearchResult ToSearchResult(TorrentDetails r)
    {
        var tags = new List<TorrentTag>();
        if (r.ParsedInfo != null)
        {
            if (!string.IsNullOrEmpty(r.ParsedInfo.Resolution)) tags.Add(new TorrentTag("quality", r.ParsedInfo.Resolution));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Quality)) tags.Add(new TorrentTag("source", r.ParsedInfo.Quality));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Codec)) tags.Add(new TorrentTag("codec", r.ParsedInfo.Codec));
            if (r.ParsedInfo.Year > 0) tags.Add(new TorrentTag("year", r.ParsedInfo.Year.ToString()));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Audio)) tags.Add(new TorrentTag("voice", r.ParsedInfo.Audio));
        }

        return new TorrentSearchResult(
            Id: r.InfoHash ?? r.Url ?? Guid.NewGuid().ToString(),
            Title: r.Title,
            Tracker: r.TrackerName,
            SizeBytes: (long)r.Size,
            Seeders: r.Sid,
            Leechers: r.Pir,
            PublishDate: r.CreateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            MagnetUri: r.Magnet,
            Link: r.Url,
            Tags: tags
        );
    }

    private async Task AttachOverridesAsync(List<TorrentSearchResult> results)
    {
        var hashes = results.Select(r => r.Id).Where(IsInfoHash).ToArray();
        var summaries = await _overrides.GetSummariesAsync(hashes);
        if (summaries.Count == 0) return;

        for (var i = 0; i < results.Count; i++)
        {
            var summary = summaries.GetValueOrDefault(results[i].Id.ToLower());
            if (summary is not null)
                results[i] = results[i] with { Override = summary };
        }
    }

    // Infohashes stored on torrent_overrides are 40-char hex; skip Guid/url fallbacks used as Id.
    private static bool IsInfoHash(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 40) return false;
        foreach (var c in id)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
