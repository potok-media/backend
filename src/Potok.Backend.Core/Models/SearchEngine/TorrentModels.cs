namespace Potok.Backend.Core.Models.SearchEngine;

public record TorrentSearchRequest(
    string Query,
    string MediaType,
    string? EnglishTitle = null,
    IEnumerable<string>? Genres = null,
    long? Id = null,
    string? OriginalTitle = null,
    string? Title = null,
    string? Year = null,
    bool? ForceSearch = null
);

public record TorrentTag(string Kind, string Value);

public record TorrentSearchResult(
    string Id,
    string Title,
    string? Tracker = null,
    long? SizeBytes = null,
    string? SizeLabel = null,
    int? Seeders = null,
    int? Leechers = null,
    string? PublishDate = null,
    string? MagnetUri = null,
    string? Link = null,
    IEnumerable<TorrentTag>? Tags = null,
    bool? Viewed = null
);

public record TorrentSearchResponse(IEnumerable<TorrentSearchResult> Results);

public record TorrentFileItem(
    string Id,
    string? Title,
    string? SizeLabel,
    long? SizeBytes,
    string? Path,
    int? Season,
    int? Episode,
    bool IsSerial,
    string FolderName,
    string Extension
);

public record TorrentFilesResponse(string? Hash, IEnumerable<TorrentFileItem>? Items);

public record TorrentFilesRequest(
    string Title,
    string? EnglishTitle = null,
    string? Link = null,
    string? MagnetUri = null,
    string? MediaType = null,
    int? NumberOfSeasons = null,
    string? OriginalTitle = null,
    string? Poster = null,
    long? TmdbId = null
);

public record TorrentStreamRequest(
    string Hash,
    string Index,
    string Path,
    string? EnglishTitle = null,
    int? Episode = null,
    string? MagnetUri = null,
    string? MediaType = null,
    string? OriginalTitle = null,
    int? Season = null,
    string? Title = null,
    long? TmdbId = null
);

public record TorrentStreamResponse(string? StreamUrl);

// Per-season override model (stored in SearchEngine, keyed by torrent infohash). season_map keys are source-season
// numbers as strings; the sentinel "_" buckets files with no parseable season. Each entry remaps that source
// season to a TMDB (Season, Offset): displayedEpisode = parsedEpisode + Offset.
public record SeasonOverrideEntry(int Season, int Offset);

public record TorrentOverrideMap(string Hash, Dictionary<string, SeasonOverrideEntry> SeasonMap);

public record UpsertSeasonOverrideRequest(int? SourceSeason, int TargetSeason, int Offset);
