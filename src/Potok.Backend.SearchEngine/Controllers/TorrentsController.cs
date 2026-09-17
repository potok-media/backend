using Microsoft.AspNetCore.Mvc;

namespace Potok.Backend.SearchEngine.Controllers;

[ApiController]
[Route("api/v1/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ISearchService _searchService;

    public TorrentsController(ISearchService _searchService)
    {
        this._searchService = _searchService;
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

        var results = await _searchService.SearchTorrentsAsync(internalRequest);

        var sharedResults = results.Select(r => {
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
        });

        return Ok(new TorrentSearchResponse(sharedResults));
    }
}
