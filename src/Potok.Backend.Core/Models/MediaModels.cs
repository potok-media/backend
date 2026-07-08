using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Potok.Backend.Core.Models;

public record MediaCard(
    [property: JsonPropertyName("id"), JsonProperty("id")] long Id,
    [property: JsonPropertyName("title"), JsonProperty("title")] string Title,
    [property: JsonPropertyName("originalTitle"), JsonProperty("originalTitle")] string? OriginalTitle = null,
    [property: JsonPropertyName("englishTitle"), JsonProperty("englishTitle")] string? EnglishTitle = null,
    [property: JsonPropertyName("subtitle"), JsonProperty("subtitle")] string? Subtitle = null,
    [property: JsonPropertyName("badgeText"), JsonProperty("badgeText")] string? BadgeText = null,
    [property: JsonPropertyName("posterSrc"), JsonProperty("posterSrc")] string? PosterSrc = null,
    [property: JsonPropertyName("backdropSrc"), JsonProperty("backdropSrc")] string? BackdropSrc = null,
    [property: JsonPropertyName("logoSrc"), JsonProperty("logoSrc")] string? LogoSrc = null,
    [property: JsonPropertyName("studioLogoSrc"), JsonProperty("studioLogoSrc")] string? StudioLogoSrc = null,
    [property: JsonPropertyName("mediaType"), JsonProperty("mediaType")] string MediaType = "movie",
    [property: JsonPropertyName("overview"), JsonProperty("overview")] string? Overview = null,
    [property: JsonPropertyName("genres"), JsonProperty("genres")] string? Genres = null,
    [property: JsonPropertyName("ageRating"), JsonProperty("ageRating")] string? AgeRating = null,
    [property: JsonPropertyName("tmdbRating"), JsonProperty("tmdbRating")] double? TmdbRating = null,
    [property: JsonPropertyName("imdbRating"), JsonProperty("imdbRating")] double? ImdbRating = null,
    [property: JsonPropertyName("kpRating"), JsonProperty("kpRating")] double? KpRating = null,
    [property: JsonPropertyName("numberOfSeasons"), JsonProperty("numberOfSeasons")] int? NumberOfSeasons = null,
    [property: JsonPropertyName("progress"), JsonProperty("progress")] WatchProgress? Progress = null,
    [property: JsonPropertyName("isInWatchlist"), JsonProperty("isInWatchlist")] bool IsInWatchlist = false,
    [property: JsonPropertyName("isFavorite"), JsonProperty("isFavorite")] bool IsFavorite = false,
    [property: JsonPropertyName("nextEpisodeNumber"), JsonProperty("nextEpisodeNumber")] int? NextEpisodeNumber = null,
    [property: JsonPropertyName("nextEpisodeSeason"), JsonProperty("nextEpisodeSeason")] int? NextEpisodeSeason = null,
    [property: JsonPropertyName("nextEpisodeTitle"), JsonProperty("nextEpisodeTitle")] string? NextEpisodeTitle = null,
    [property: JsonPropertyName("airDateTime"), JsonProperty("airDateTime")] DateTime? AirDateTime = null,
    [property: JsonPropertyName("cast"), JsonProperty("cast")] IEnumerable<MediaCredit>? Cast = null,
    [property: JsonPropertyName("directors"), JsonProperty("directors")] IEnumerable<MediaCredit>? Directors = null,
    [property: JsonPropertyName("kpId"), JsonProperty("kpId")] string? KpId = null,
    [property: JsonPropertyName("imdbId"), JsonProperty("imdbId")] string? ImdbId = null
);

public record WatchProgress(
    [property: JsonPropertyName("aired"), JsonProperty("aired")] int Aired,
    [property: JsonPropertyName("completed"), JsonProperty("completed")] int Completed,
    [property: JsonPropertyName("lastEpisodeTitle"), JsonProperty("lastEpisodeTitle")] string? LastEpisodeTitle = null,
    [property: JsonPropertyName("lastSeason"), JsonProperty("lastSeason")] int? LastSeason = null,
    [property: JsonPropertyName("lastEpisode"), JsonProperty("lastEpisode")] int? LastEpisode = null,
    [property: JsonPropertyName("nextEpisodeTitle"), JsonProperty("nextEpisodeTitle")] string? NextEpisodeTitle = null,
    [property: JsonPropertyName("nextSeason"), JsonProperty("nextSeason")] int? NextSeason = null,
    [property: JsonPropertyName("nextEpisode"), JsonProperty("nextEpisode")] int? NextEpisode = null,
    [property: JsonPropertyName("watchedEpisodes"), JsonProperty("watchedEpisodes")] List<WatchedEpisode>? WatchedEpisodes = null
);

public record WatchedEpisode(
    [property: JsonPropertyName("season"), JsonProperty("season")] int Season, 
    [property: JsonPropertyName("number"), JsonProperty("number")] int Number
);

public record MediaCredit(
    [property: JsonPropertyName("id"), JsonProperty("id")] long Id,
    [property: JsonPropertyName("name"), JsonProperty("name")] string Name,
    [property: JsonPropertyName("role"), JsonProperty("role")] string? Role,
    [property: JsonPropertyName("imageSrc"), JsonProperty("imageSrc")] string? ImageSrc
);

public record MediaEpisode(
    [property: JsonPropertyName("id"), JsonProperty("id")] long Id,
    [property: JsonPropertyName("name"), JsonProperty("name")] string? Name,
    [property: JsonPropertyName("overview"), JsonProperty("overview")] string? Overview,
    [property: JsonPropertyName("episodeNumber"), JsonProperty("episodeNumber")] int EpisodeNumber,
    [property: JsonPropertyName("seasonNumber"), JsonProperty("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("airDate"), JsonProperty("airDate")] string? AirDate,
    [property: JsonPropertyName("stillPath"), JsonProperty("stillPath")] string? StillPath,
    [property: JsonPropertyName("voteAverage"), JsonProperty("voteAverage")] double? VoteAverage,
    [property: JsonPropertyName("cast"), JsonProperty("cast")] IEnumerable<MediaCredit>? Cast = null,
    [property: JsonPropertyName("directors"), JsonProperty("directors")] IEnumerable<MediaCredit>? Directors = null
);

public record MediaSeason(
    [property: JsonPropertyName("id"), JsonProperty("id")] long Id,
    [property: JsonPropertyName("name"), JsonProperty("name")] string? Name,
    [property: JsonPropertyName("overview"), JsonProperty("overview")] string? Overview,
    [property: JsonPropertyName("seasonNumber"), JsonProperty("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("posterPath"), JsonProperty("posterPath")] string? PosterPath,
    [property: JsonPropertyName("episodes"), JsonProperty("episodes")] IEnumerable<MediaEpisode>? Episodes,
    [property: JsonPropertyName("cast"), JsonProperty("cast")] IEnumerable<MediaCredit>? Cast = null,
    [property: JsonPropertyName("directors"), JsonProperty("directors")] IEnumerable<MediaCredit>? Directors = null
);

public record MediaRow(
    [property: JsonPropertyName("id"), JsonProperty("id")] string Id,
    [property: JsonPropertyName("title"), JsonProperty("title")] string Title,
    [property: JsonPropertyName("items"), JsonProperty("items")] IEnumerable<MediaCard> Items
);

public record HeroItem(
    [property: JsonPropertyName("id"), JsonProperty("id")] long Id,
    [property: JsonPropertyName("card"), JsonProperty("card")] MediaCard Card,
    [property: JsonPropertyName("backdropSrc"), JsonProperty("backdropSrc")] string? BackdropSrc = null
);

public record HomeResponse(
    [property: JsonPropertyName("hero"), JsonProperty("hero")] IEnumerable<HeroItem> Hero,
    [property: JsonPropertyName("rows"), JsonProperty("rows")] IEnumerable<MediaRow> Rows,
    [property: JsonPropertyName("nextCursor"), JsonProperty("nextCursor")] string? NextCursor = null
);
