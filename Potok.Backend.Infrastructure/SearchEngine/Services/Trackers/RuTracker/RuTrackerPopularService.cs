using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;

/// <summary>
///     Сервис обновления популярных раздач по категориям
/// </summary>
public class RuTrackerPopularService : BaseRuTracker
{
    private readonly ITorrentRepository _torrentRepository;

    public RuTrackerPopularService(IOptions<Config> config, HttpService httpService, ICacheService cacheService,
        ITorrentRepository torrentRepository) : base(config, httpService, cacheService)
    {
        _torrentRepository = torrentRepository;
    }

    public override async Task InvokeAsync()
    {
        if (!Config.RuTracker.Popular.Enable)
            return;

        var categories = Config.RuTracker.Popular.Categories;
        var now = DateTime.UtcNow;

        foreach (var category in categories)
        {
            var url = BuildCategoryUrl(Host, category.ToString(), 0);
            var html = await Get(
                url,
                RuEncoding,
                url
                /*useProxy: useProxy*/);
            var torrents = ParseForumPage(html, category.ToString(), Host, now);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };
            await Parallel.ForEachAsync(
                torrents,
                options,
                async (torrent, _) =>
                {
                    await _torrentRepository.AddOrUpdateAsync(
                        [torrent],
                        FetchDetailsAsync);
                });

            var maxPage = GetMaxPages(html);
            if (maxPage == 0) continue;

            var maxPages = Config.RuTracker.Popular.MaxPages;
            if (maxPage <= maxPages)
                maxPages = maxPage;

            for (var page = 1; page < maxPages; page++)
            {
                url = BuildCategoryUrl(Host, category.ToString(), page);
                torrents = await FetchForumPageAsync(url, category.ToString(), now);

                await Parallel.ForEachAsync(
                    torrents,
                    options,
                    async (torrent, _) =>
                    {
                        await _torrentRepository.AddOrUpdateAsync(
                            [torrent],
                            FetchDetailsAsync);
                    });
            }
        }
    }
}