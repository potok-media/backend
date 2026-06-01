using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models;

public record TmdbMovie(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("original_title")] string? OriginalTitle,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropSrc,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("original_language")] string? OriginalLanguage,
    [property: JsonPropertyName("genres")] List<TmdbGenre>? Genres,
    [property: JsonPropertyName("production_companies")] List<TmdbProductionCompany>? ProductionCompanies,
    [property: JsonPropertyName("images")] TmdbImageContainer? Images,
    [property: JsonPropertyName("translations")] TmdbTranslationContainer? Translations,
    [property: JsonPropertyName("credits")] TmdbCredits? Credits,
    [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds = null
);

public record TmdbTvShow(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("original_name")] string? OriginalName,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropSrc,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("original_language")] string? OriginalLanguage,
    [property: JsonPropertyName("number_of_seasons")] int? NumberOfSeasons,
    [property: JsonPropertyName("genres")] List<TmdbGenre>? Genres,
    [property: JsonPropertyName("networks")] List<TmdbProductionCompany>? Networks,
    [property: JsonPropertyName("production_companies")] List<TmdbProductionCompany>? ProductionCompanies,
    [property: JsonPropertyName("seasons")] List<TmdbSeason>? Seasons,
    [property: JsonPropertyName("images")] TmdbImageContainer? Images,
    [property: JsonPropertyName("translations")] TmdbTranslationContainer? Translations,
    [property: JsonPropertyName("credits")] TmdbCredits? Credits,
    [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds = null
);

public record TmdbSeason(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("season_number")] int SeasonNumber,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("episodes")] List<TmdbEpisode>? Episodes,
    [property: JsonPropertyName("credits")] TmdbCredits? Credits
);

public record TmdbEpisode(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("episode_number")] int EpisodeNumber,
    [property: JsonPropertyName("season_number")] int SeasonNumber,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("still_path")] string? StillPath,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("guest_stars")] List<TmdbCast>? GuestStars,
    [property: JsonPropertyName("crew")] List<TmdbCrew>? Crew
);

public record TmdbCredits(
    [property: JsonPropertyName("cast")] List<TmdbCast>? Cast,
    [property: JsonPropertyName("crew")] List<TmdbCrew>? Crew
);

public record TmdbCast(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("character")] string? Character,
    [property: JsonPropertyName("profile_path")] string? ProfilePath,
    [property: JsonPropertyName("order")] int Order
);

public record TmdbCrew(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("job")] string? Job,
    [property: JsonPropertyName("department")] string? Department,
    [property: JsonPropertyName("profile_path")] string? ProfilePath
);

public record TmdbImageContainer(
    [property: JsonPropertyName("logos")] List<TmdbLogo>? Logos
);

public record TmdbGenre(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name
);

public record TmdbProductionCompany(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("logo_path")] string? LogoPath,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("origin_country")] string? OriginCountry
);

public record TmdbLogo(
    [property: JsonPropertyName("iso_639_1")] string? Iso639_1,
    [property: JsonPropertyName("file_path")] string? FilePath
);

public record TmdbTranslationContainer(
    [property: JsonPropertyName("translations")] List<TmdbTranslation>? Translations
);

public record TmdbTranslation(
    [property: JsonPropertyName("iso_639_1")] string? Iso639_1,
    [property: JsonPropertyName("iso_3166_1")] string? Iso3166_1,
    [property: JsonPropertyName("data")] TmdbTranslationData? Data
);

public record TmdbTranslationData(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview
);

public record TmdbMultiSearchResult(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate
);

public record TmdbPagedResponse<T>(
    [property: JsonPropertyName("results")] IEnumerable<T> Results,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("total_results")] int TotalResults
);

public record TmdbExternalIds(
    [property: JsonPropertyName("imdb_id")] string? ImdbId,
    [property: JsonPropertyName("wikidata_id")] string? WikidataId
);
