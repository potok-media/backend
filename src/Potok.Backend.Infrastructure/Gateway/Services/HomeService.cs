using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class HomeService : IHomeService
{
    private const int DedupPriorityRowCount = 3;
    private const int RowTargetCount = 10;  // every row must show exactly this many cards
    private const int RowBuffer = 24;       // overfetch target after filtering — headroom for dedup
    private const int MaxPagesPerRow = 3;   // cap TMDB pages we'll pull to reach the buffer

    // Keep the home feed to Western content: drop anime and Korean/Chinese/Indian/other Asian titles by their
    // original language or origin country (TMDB discover has no "exclude language", so we filter the response).
    private static readonly HashSet<string> BlockedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", // Japanese (anime + J-dramas)
        "ko", // Korean
        "zh", "cn", // Chinese / Cantonese
        "hi", "ta", "te", "ml", "kn", "bn", "mr", "pa", // Indian languages
        "th", // Thai
    };

    private static readonly HashSet<string> BlockedCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "JP", "KR", "CN", "HK", "TW", "IN", "TH",
    };

    private static bool IsAllowed(TmdbMultiSearchResult item)
    {
        if (item.OriginalLanguage != null && BlockedLanguages.Contains(item.OriginalLanguage))
        {
            return false;
        }

        if (item.OriginCountry != null && item.OriginCountry.Any(c => c != null && BlockedCountries.Contains(c)))
        {
            return false;
        }

        return true;
    }

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
                // Pull pages until we have enough ALLOWED items to survive filtering + dedup and still hit 10.
                var collected = new List<TmdbMultiSearchResult>();
                for (var page = 1; page <= MaxPagesPerRow && collected.Count < RowBuffer; page++)
                {
                    var url = def.Path + (def.Path.Contains('?') ? "&" : "?") + "page=" + page;
                    var payload = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMultiSearchResult>>(url);
                    if (payload?.Results == null) break;

                    collected.AddRange(payload.Results.Where(IsAllowed));
                    if (page >= payload.TotalPages) break;
                }

                if (collected.Count == 0) return null;

                var items = collected
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

                var heroRawItems = trending.Results.Where(IsAllowed).Take(10).ToList();

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
            rowIndex++;

            // Priority rows may overlap the hero/each other; others drop anything already shown above.
            var candidates = isPriority
                ? row.Items
                : row.Items.Where(item => !seen.Contains((item.MediaType, item.Id)));

            var items = candidates.Take(RowTargetCount).ToList();

            // Hard rule: a row is shown only if it can fill exactly RowTargetCount cards — otherwise hide it.
            if (items.Count < RowTargetCount)
            {
                continue;
            }

            foreach (var item in items)
            {
                seen.Add((item.MediaType, item.Id));
            }

            result.Add(new MediaRow(row.Id, row.Title, items));
        }

        return result;
    }
}