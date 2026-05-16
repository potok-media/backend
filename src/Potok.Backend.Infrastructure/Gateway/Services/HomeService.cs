using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class HomeService : IHomeService
{
    private readonly TmdbClient _tmdbClient;

    private static readonly List<RowDefinition> RowDefinitions = new()
    {
        new("movie.now-playing", "В кинотеатрах", "movie/now_playing", "movie"),
        new("movie.trending-day", "Тренды дня", "trending/movie/day", "movie"),
        new("movie.trending-week", "Тренды недели", "trending/movie/week", "movie"),
        new("movie.upcoming", "Скоро выйдет", "movie/upcoming", "movie"),
        new("movie.popular", "Популярные фильмы", "movie/popular", "movie"),
        new("tv.popular", "Популярные сериалы", "trending/tv/week", "tv"),
        new("movie.top-rated", "Лучшие фильмы", "movie/top_rated", "movie"),
        new("tv.top-rated", "Лучшие сериалы", "tv/top_rated", "tv")
    };

    public HomeService(TmdbClient tmdbClient)
    {
        _tmdbClient = tmdbClient;
    }

    public async Task<HomeResponse> GetHomeFeedAsync(
        string? cursor = null, 
        string? baseUrl = null, 
        string posterSize = "w780", 
        string backdropSize = "w1280", 
        string logoSize = "original")
    {
        // 1. Start fetching rows in parallel
        var rowsTasks = RowDefinitions.Select(async def =>
        {
            try
            {
                var payload = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMultiSearchResult>>(def.Path);
                if (payload?.Results == null) return null;

                var items = payload.Results
                    .Select(item => MediaMapper.MapToMediaCard(item with { MediaType = def.MediaType }, baseUrl, posterSize, backdropSize));

                return new MediaRow(def.Id, def.Title, items);
            }
            catch
            {
                return null;
            }
        });
        var allRowsTask = Task.WhenAll(rowsTasks);

        // 2. Start fetching hero items (requires trending list first, then details)
        async Task<IEnumerable<HeroItem>> GetHeroItemsInternalAsync()
        {
            try
            {
                var trending = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMultiSearchResult>>("trending/all/week");
                if (trending?.Results == null) return Enumerable.Empty<HeroItem>();

                var heroRawItems = trending.Results.Take(10).ToList();

                var heroItemsTasks = heroRawItems.Select(async item =>
                {
                    try
                    {
                        var id = item.Id;
                        var mediaType = item.MediaType ?? "movie";

                        // Get full detail with images for the logo
                        if (mediaType == "movie")
                        {
                            var movie = await _tmdbClient.GetAsync<TmdbMovie>($"{mediaType}/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
                            if (movie == null) return null;
                            var card = MediaMapper.MapToMediaCard(movie, baseUrl, posterSize, backdropSize, logoSize);
                            return new HeroItem(Id: card.Id, Card: card, BackdropSrc: card.BackdropSrc);
                        }
                        else
                        {
                            var tv = await _tmdbClient.GetAsync<TmdbTvShow>($"{mediaType}/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
                            if (tv == null) return null;
                            var card = MediaMapper.MapToMediaCard(tv, baseUrl, posterSize, backdropSize, logoSize);
                            return new HeroItem(Id: card.Id, Card: card, BackdropSrc: card.BackdropSrc);
                        }
                    }
                    catch
                    {
                        return null;
                    }
                });

                var results = await Task.WhenAll(heroItemsTasks);
                return results.Where(h => h != null).Cast<HeroItem>();
            }
            catch
            {
                return Enumerable.Empty<HeroItem>();
            }
        }
        
        var heroTask = GetHeroItemsInternalAsync();

        // Wait for both tracks to complete
        await Task.WhenAll(allRowsTask, heroTask);

        var rows = (await allRowsTask).Where(r => r != null).Cast<MediaRow>();
        var heroItems = await heroTask;

        return new HomeResponse(heroItems, rows, null);
    }

    private record RowDefinition(string Id, string Title, string Path, string MediaType);
}
