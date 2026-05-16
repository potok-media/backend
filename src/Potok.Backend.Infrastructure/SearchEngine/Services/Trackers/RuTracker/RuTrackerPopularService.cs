using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;

/// <summary>
///     Сервис обновления популярных раздач по категориям
/// </summary>
public class RuTrackerPopularService : BaseRuTracker
{
    private readonly ITorrentRepository _torrentRepository;

    public RuTrackerPopularService(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService,
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

        var semaphore = new SemaphoreSlim(3);
        foreach (var category in categories)
        {
            var url = BuildCategoryUrl(Host, category.ToString(), 0);
            var html = await Get(
                url,
                RuEncoding,
                url,
                ct: CancellationToken.None);
            var torrents = ParseForumPage(html, category.ToString(), Host, now);

            var tasks = torrents.Select(async torrent =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await _torrentRepository.AddOrUpdateAsync(
                        [torrent],
                        (t, ct) => FetchDetailsAsync(t, ct),
                        CancellationToken.None);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);

            var maxPage = GetMaxPages(html);
            if (maxPage == 0) continue;

            var maxPages = Config.RuTracker.Popular.MaxPages;
            if (maxPage <= maxPages)
                maxPages = maxPage;

            for (var page = 1; page < maxPages; page++)
            {
                url = BuildCategoryUrl(Host, category.ToString(), page);
                torrents = await FetchForumPageAsync(url, category.ToString(), now, CancellationToken.None);

                var pageTasks = torrents.Select(async torrent =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await _torrentRepository.AddOrUpdateAsync(
                            [torrent],
                            (t, ct) => FetchDetailsAsync(t, ct),
                            CancellationToken.None);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(pageTasks);
            }
        }
    }
}