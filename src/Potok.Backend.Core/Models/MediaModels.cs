namespace Potok.Backend.Core.Models;

public record MediaCard(
    string Id,
    string Title,
    string? OriginalTitle = null,
    string? EnglishTitle = null,
    string? Subtitle = null,
    string? BadgeText = null,
    string? PosterSrc = null,
    string? BackdropSrc = null,
    string? LogoSrc = null,
    string? StudioLogoSrc = null,
    string MediaType = "movie",
    string? Overview = null,
    string? Genres = null,
    string? AgeRating = null,
    double? ImdbRating = null,
    double? KpRating = null,
    int? NumberOfSeasons = null,
    WatchProgress? Progress = null,
    bool IsInWatchlist = false,
    bool IsFavorite = false,
    int? NextEpisodeNumber = null,
    int? NextEpisodeSeason = null,
    string? NextEpisodeTitle = null,
    DateTime? AirDateTime = null,
    IEnumerable<MediaCredit>? Cast = null,
    IEnumerable<MediaCredit>? Directors = null
);

public record WatchProgress(
    int Aired,
    int Completed,
    string? LastEpisodeTitle = null,
    int? LastSeason = null,
    int? LastEpisode = null,
    string? NextEpisodeTitle = null,
    int? NextSeason = null,
    int? NextEpisode = null,
    List<WatchedEpisode>? WatchedEpisodes = null
);

public record WatchedEpisode(int Season, int Number);

public record MediaCredit(
    string Name,
    string? Role,
    string? ImageSrc
);

public record MediaEpisode(
    int Id,
    string? Name,
    string? Overview,
    int EpisodeNumber,
    int SeasonNumber,
    string? AirDate,
    string? StillPath,
    double? VoteAverage,
    IEnumerable<MediaCredit>? Cast = null,
    IEnumerable<MediaCredit>? Directors = null
);

public record MediaSeason(
    int Id,
    string? Name,
    string? Overview,
    int SeasonNumber,
    string? PosterPath,
    IEnumerable<MediaEpisode>? Episodes,
    IEnumerable<MediaCredit>? Cast = null,
    IEnumerable<MediaCredit>? Directors = null
);

public record MediaRow(
    string Id,
    string Title,
    IEnumerable<MediaCard> Items
);

public record HeroItem(
    string Id,
    MediaCard Card,
    string? BackdropSrc = null
);

public record HomeResponse(
    IEnumerable<HeroItem> Hero,
    IEnumerable<MediaRow> Rows,
    string? NextCursor = null
);
