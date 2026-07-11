namespace Potok.Backend.Infrastructure.Gateway.Services;

public record HomeRowDefinition(string Id, string Title, string Path, string MediaType);

public static class HomeRowCatalog
{
    private const int RotatingInsertIndex = 7;

    private static readonly HomeRowDefinition[] FixedRows =
    [
        new("tv.on-the-air", "В эфире", "tv/on_the_air", "tv"),
        new("tv.airing-today", "Сегодня в эфире", "tv/airing_today", "tv"),
        new("movie.now-playing", "В кинотеатрах", "movie/now_playing", "movie"),
        new("mood.friday-thrills", "Адреналин", "discover/movie?with_genres=28,53&sort_by=popularity.desc", "movie"),
        new("mood.cozy-evening", "Уютный вечер", "discover/tv?with_genres=18,10749&sort_by=popularity.desc", "tv"),
        new("discover.hidden-gems", "Скрытые жемчужины", "discover/movie?vote_average.gte=7.5&vote_count.gte=200&sort_by=vote_average.desc", "movie"),
        new("genre.movie.27", "Ночной сеанс", "discover/movie?with_genres=27&sort_by=popularity.desc", "movie"),
        new("movie.upcoming", "Скоро в кино", "movie/upcoming", "movie"),
        new("tv.top-rated", "Лучшие сериалы", "tv/top_rated", "tv"),
        new("movie.short-runtime", "Один вечер — один фильм", "discover/movie?with_runtime.lte=100&sort_by=popularity.desc", "movie"),
    ];

    private static readonly HomeRowDefinition[] RotatingRows =
    [
        new("genre.movie.35", "Комедийный перерыв", "discover/movie?with_genres=35&sort_by=popularity.desc", "movie"),
        new("genre.movie.878", "Космос и будущее", "discover/movie?with_genres=878&sort_by=popularity.desc", "movie"),
        new("genre.tv.10765", "Sci-Fi сериалы", "discover/tv?with_genres=10765&sort_by=popularity.desc", "tv"),
        new("mood.nostalgia", "Ностальгия 90-х", "discover/movie?primary_release_date.gte=1990-01-01&primary_release_date.lte=1999-12-31&sort_by=popularity.desc", "movie"),
    ];

    private static readonly Dictionary<string, HomeRowDefinition> AllDefinitions;

    static HomeRowCatalog()
    {
        AllDefinitions = FixedRows
            .Concat(RotatingRows)
            .ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<HomeRowDefinition> GetActiveRows()
    {
        var rotating = RotatingRows[(int)DateTime.UtcNow.DayOfWeek % RotatingRows.Length];
        var rows = new List<HomeRowDefinition>(FixedRows);
        rows.Insert(RotatingInsertIndex, rotating);
        return rows;
    }

    public static bool TryGetDefinition(string rowId, out HomeRowDefinition definition)
    {
        if (AllDefinitions.TryGetValue(rowId, out var found))
        {
            definition = found;
            return true;
        }

        definition = default!;
        return false;
    }
}