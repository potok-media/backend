using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Mappers;

public static class MediaMapper
{
    public static MediaCard MapToMediaCard(
        TmdbMovie movie, 
        string? baseUrl = null, 
        string posterSize = "w780", 
        string backdropSize = "original", 
        string logoSize = "original")
    {
        var id = movie.Id;
        var title = movie.Title ?? "Unknown";
        var originalTitle = movie.OriginalTitle;
        
        var releaseDate = movie.ReleaseDate ?? "";
        var year = releaseDate.Length >= 4 ? releaseDate[..4] : "";
        
        var voteAverage = movie.VoteAverage;
        
        var subtitleParts = new List<string>();
        if (!string.IsNullOrEmpty(year)) subtitleParts.Add(year);

        var posterPath = BuildUrl(baseUrl, "poster", posterSize, movie.PosterPath);
        var backdropPath = BuildUrl(baseUrl, "backdrop", backdropSize, movie.BackdropSrc);
        string? logoPath = null;

        if (movie.Images?.Logos != null)
        {
            var logoEntry = movie.Images.Logos.FirstOrDefault(l => l.Iso639_1 == "ru") 
                ?? movie.Images.Logos.FirstOrDefault(l => l.Iso639_1 == "en")
                ?? movie.Images.Logos.FirstOrDefault();
            
            logoPath = BuildUrl(baseUrl, "logo", logoSize, logoEntry?.FilePath);
        }

        string? englishTitle = null;
        if (movie.Translations?.Translations != null)
        {
            var enEntry = movie.Translations.Translations
                .FirstOrDefault(t => t.Iso639_1 == "en" && t.Iso3166_1 == "US")
                ?? movie.Translations.Translations.FirstOrDefault(t => t.Iso639_1 == "en");

            englishTitle = enEntry?.Data?.Title ?? enEntry?.Data?.Name;
        }

        if (string.IsNullOrEmpty(englishTitle) && movie.OriginalLanguage == "en")
        {
            englishTitle = originalTitle;
        }
        
        string? genres = null;
        if (movie.Genres != null && movie.Genres.Any())
        {
            genres = string.Join(", ", movie.Genres.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)));
        }

        string? studioLogoPath = null;
        var firstCompanyWithLogo = movie.ProductionCompanies?.FirstOrDefault(c => !string.IsNullOrEmpty(c.LogoPath));
        if (firstCompanyWithLogo != null)
        {
            studioLogoPath = BuildUrl(baseUrl, "logo", logoSize, firstCompanyWithLogo.LogoPath);
        }
        
        return new MediaCard(
            Id: id,
            Title: title,
            OriginalTitle: originalTitle,
            EnglishTitle: englishTitle,
            Subtitle: string.Join(" • ", subtitleParts),
            PosterSrc: posterPath,
            BackdropSrc: backdropPath,
            LogoSrc: logoPath,
            StudioLogoSrc: studioLogoPath,
            MediaType: "movie",
            Overview: movie.Overview,
            Genres: genres,
            TmdbRating: voteAverage > 0 ? voteAverage : null,
            ImdbRating: null,
            IsInWatchlist: false,
            IsFavorite: false,
            Cast: MapCredits(movie.Credits?.Cast, baseUrl),
            Directors: MapCrew(movie.Credits?.Crew, baseUrl, "Director"),
            ImdbId: movie.ExternalIds?.ImdbId
        );
    }

    public static MediaCard MapToMediaCard(
        TmdbTvShow tvShow, 
        string? baseUrl = null, 
        string posterSize = "w780", 
        string backdropSize = "original", 
        string logoSize = "original")
    {
        var id = tvShow.Id;
        var title = tvShow.Name ?? "Unknown";
        var originalTitle = tvShow.OriginalName;
        
        var releaseDate = tvShow.FirstAirDate ?? "";
        var year = releaseDate.Length >= 4 ? releaseDate[..4] : "";
        
        var voteAverage = tvShow.VoteAverage;
        
        var subtitleParts = new List<string>();
        if (!string.IsNullOrEmpty(year)) subtitleParts.Add(year);

        var posterPath = BuildUrl(baseUrl, "poster", posterSize, tvShow.PosterPath);
        var backdropPath = BuildUrl(baseUrl, "backdrop", backdropSize, tvShow.BackdropSrc);
        string? logoPath = null;

        if (tvShow.Images?.Logos != null)
        {
            var logoEntry = tvShow.Images.Logos.FirstOrDefault(l => l.Iso639_1 == "ru") 
                ?? tvShow.Images.Logos.FirstOrDefault(l => l.Iso639_1 == "en")
                ?? tvShow.Images.Logos.FirstOrDefault();
            
            logoPath = BuildUrl(baseUrl, "logo", logoSize, logoEntry?.FilePath);
        }

        string? englishTitle = null;
        if (tvShow.Translations?.Translations != null)
        {
            var enEntry = tvShow.Translations.Translations
                .FirstOrDefault(t => t.Iso639_1 == "en" && t.Iso3166_1 == "US")
                ?? tvShow.Translations.Translations.FirstOrDefault(t => t.Iso639_1 == "en");

            englishTitle = enEntry?.Data?.Title ?? enEntry?.Data?.Name;
        }

        if (string.IsNullOrEmpty(englishTitle) && tvShow.OriginalLanguage == "en")
        {
            englishTitle = originalTitle;
        }
        
        string? genres = null;
        if (tvShow.Genres != null && tvShow.Genres.Any())
        {
            genres = string.Join(", ", tvShow.Genres.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)));
        }

        string? studioLogoPath = null;
        var firstNetworkWithLogo = tvShow.Networks?.FirstOrDefault(c => !string.IsNullOrEmpty(c.LogoPath)) ?? tvShow.ProductionCompanies?.FirstOrDefault(c => !string.IsNullOrEmpty(c.LogoPath));
        if (firstNetworkWithLogo != null)
        {
            studioLogoPath = BuildUrl(baseUrl, "logo", logoSize, firstNetworkWithLogo.LogoPath);
        }
        
        return new MediaCard(
            Id: id,
            Title: title,
            OriginalTitle: originalTitle,
            EnglishTitle: englishTitle,
            Subtitle: string.Join(" • ", subtitleParts),
            PosterSrc: posterPath,
            BackdropSrc: backdropPath,
            LogoSrc: logoPath,
            StudioLogoSrc: studioLogoPath,
            MediaType: "tv",
            Overview: tvShow.Overview,
            Genres: genres,
            TmdbRating: voteAverage > 0 ? voteAverage : null,
            ImdbRating: null,
            NumberOfSeasons: tvShow.NumberOfSeasons ?? tvShow.Seasons?.Count,
            IsInWatchlist: false,
            IsFavorite: false,
            Cast: MapCredits(tvShow.Credits?.Cast, baseUrl),
            Directors: MapCrew(tvShow.Credits?.Crew, baseUrl, "Director"),
            ImdbId: tvShow.ExternalIds?.ImdbId
        );
    }

    public static MediaSeason MapToMediaSeason(TmdbSeason season, string? baseUrl = null)
    {
        return new MediaSeason(
            Id: season.Id,
            Name: season.Name,
            Overview: season.Overview,
            SeasonNumber: season.SeasonNumber,
            PosterPath: BuildUrl(baseUrl, "poster", "w780", season.PosterPath),
            Episodes: season.Episodes?.Select(e => MapToMediaEpisode(e, baseUrl)),
            Cast: MapCredits(season.Credits?.Cast, baseUrl),
            Directors: MapCrew(season.Credits?.Crew, baseUrl, "Director")
        );
    }

    public static MediaEpisode MapToMediaEpisode(TmdbEpisode episode, string? baseUrl = null)
    {
        return new MediaEpisode(
            Id: episode.Id,
            Name: episode.Name,
            Overview: episode.Overview,
            EpisodeNumber: episode.EpisodeNumber,
            SeasonNumber: episode.SeasonNumber,
            AirDate: episode.AirDate,
            StillPath: BuildUrl(baseUrl, "still", "original", episode.StillPath),
            VoteAverage: episode.VoteAverage > 0 ? episode.VoteAverage : null,
            Cast: MapCredits(episode.GuestStars, baseUrl),
            Directors: MapCrew(episode.Crew, baseUrl, "Director")
        );
    }

    public static MediaCard MapToMediaCard(
        TmdbMultiSearchResult result, 
        string? baseUrl = null, 
        string posterSize = "w780", 
        string backdropSize = "original")
    {
        var id = result.Id;
        var title = result.Title ?? result.Name ?? "Unknown";
        
        var releaseDate = result.ReleaseDate ?? result.FirstAirDate ?? "";
        var year = releaseDate.Length >= 4 ? releaseDate[..4] : "";
        
        var voteAverage = result.VoteAverage;
        
        var subtitleParts = new List<string>();
        if (!string.IsNullOrEmpty(year)) subtitleParts.Add(year);

        var posterPath = BuildUrl(baseUrl, "poster", posterSize, result.PosterPath);
        var backdropPath = BuildUrl(baseUrl, "backdrop", backdropSize, result.BackdropPath);
        
        return new MediaCard(
            Id: id,
            Title: title,
            Subtitle: string.Join(" • ", subtitleParts),
            PosterSrc: posterPath,
            BackdropSrc: backdropPath,
            MediaType: result.MediaType,
            TmdbRating: voteAverage > 0 ? voteAverage : null
        );
    }

    private static string? BuildUrl(string? baseUrl, string role, string size, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (string.IsNullOrEmpty(baseUrl)) return path;

        var cleanPath = path.TrimStart('/');
        return $"{baseUrl.TrimEnd('/')}/media/tmdb/t/p/{size}/{cleanPath}";
    }

    private static IEnumerable<MediaCredit>? MapCredits(IEnumerable<TmdbCast>? cast, string? baseUrl)
    {
        return cast?.OrderBy(c => c.Order).Take(50).Select(c => new MediaCredit(
            Name: c.Name ?? "Unknown",
            Role: c.Character,
            ImageSrc: BuildUrl(baseUrl, "profile", "w185", c.ProfilePath)
        ));
    }

    private static IEnumerable<MediaCredit>? MapCrew(IEnumerable<TmdbCrew>? crew, string? baseUrl, string job)
    {
        return crew?.Where(c => c.Job == job).Select(c => new MediaCredit(
            Name: c.Name ?? "Unknown",
            Role: c.Job,
            ImageSrc: BuildUrl(baseUrl, "profile", "w185", c.ProfilePath)
        ));
    }
}
