using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class HomeService : IHomeService
{
    private const int DedupPriorityRowCount = 3;

    private readonly TmdbClient _tmdbClient;

    public HomeService(TmdbClient tmdbClient)
    {
        _tmdbClient = tmdbClient;
    }

    public async Task<HomeResponse> GetHomeFeedAsync(
        string? baseUrl = null,
        string posterSize = "w780",
        string backdropSize = "w1280",
        string logoSize = "original")
    {
        var targetRowDefinitions = HomeRowCatalog.GetActiveRows();

        var rowsTasks = targetRowDefinitions.Select(async def =>
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

                        if (mediaType == "movie")
                        {
                            var movie = await _tmdbClient.GetAsync<TmdbMovie>($"{mediaType}/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
                            if (movie == null) return null;
                            var card = MediaMapper.MapToMediaCard(movie, baseUrl, posterSize, backdropSize, logoSize, _tmdbClient.CurrentLanguage);
                            return new HeroItem(Id: card.Id, Card: card, BackdropSrc: card.BackdropSrc);
                        }

                        var tv = await _tmdbClient.GetAsync<TmdbTvShow>($"{mediaType}/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
                        if (tv == null) return null;
                        var tvCard = MediaMapper.MapToMediaCard(tv, baseUrl, posterSize, backdropSize, logoSize, _tmdbClient.CurrentLanguage);
                        return new HeroItem(Id: tvCard.Id, Card: tvCard, BackdropSrc: tvCard.BackdropSrc);
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

        await Task.WhenAll(allRowsTask, heroTask);

        var heroItems = (await heroTask).ToList();
        var rows = DeduplicateRows(heroItems, (await allRowsTask).Where(r => r != null).Cast<MediaRow>());

        return new HomeResponse(heroItems, rows, null);
    }

    private static IEnumerable<MediaRow> DeduplicateRows(
        IReadOnlyCollection<HeroItem> hero,
        IEnumerable<MediaRow> rows)
    {
        var seen = new HashSet<(string MediaType, long Id)>();
        foreach (var item in hero)
        {
            seen.Add((item.Card.MediaType, item.Card.Id));
        }

        var result = new List<MediaRow>();
        var rowIndex = 0;

        foreach (var row in rows)
        {
            var isPriority = rowIndex < DedupPriorityRowCount;
            var items = row.Items.ToList();

            if (!isPriority)
            {
                items = items
                    .Where(item => seen.Add((item.MediaType, item.Id)))
                    .ToList();
            }
            else
            {
                foreach (var item in items)
                {
                    seen.Add((item.MediaType, item.Id));
                }
            }

            if (items.Count > 0)
            {
                result.Add(new MediaRow(row.Id, row.Title, items));
            }

            rowIndex++;
        }

        return result;
    }
}