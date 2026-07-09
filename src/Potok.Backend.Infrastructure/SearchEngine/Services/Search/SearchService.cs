using Microsoft.Extensions.Options;
using Potok.Backend.Core.Models.SearchEngine.Api;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Core.Utils;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Search;

public class SearchService : BaseSearchService, ISearchService
{
    private readonly ILocalSearchService _localSearch;
    private readonly IRemoteSearchService _remoteSearch;
    private readonly ITorrentMergerService _merger;
    private readonly ITorrentRepository _repository;
    private readonly IQueriesRepository _queriesRepository;
    private readonly IMediaResolverService _mediaResolver;
    private readonly Config _config;

    public SearchService(
        IOptions<Config> config,
        TrackerHttpClient httpService,
        ICacheService cacheService,
        ILocalSearchService localSearch,
        IRemoteSearchService remoteSearch,
        ITorrentMergerService merger,
        ITorrentRepository repository,
        IQueriesRepository queriesRepository,
        IMediaResolverService mediaResolver) : base(config.Value, httpService, cacheService)
    {
        _localSearch = localSearch;
        _remoteSearch = remoteSearch;
        _merger = merger;
        _repository = repository;
        _queriesRepository = queriesRepository;
        _mediaResolver = mediaResolver;
        _config = config.Value;
    }

    public async Task<IReadOnlyCollection<TorrentDetails>> SearchTorrentsAsync(TorrentSearchQuery request)
    {
        var cacheKey = CacheKeyBuilder.Build("api", "v1.0", "torrents", 
            request.TmdbId?.ToString() ?? "null", 
            request.Query ?? "null", 
            request.Type ?? "null", 
            request.Year.ToString());

        if (request.ForceSearch)
        {
            var torrents = await ExecuteUnifiedSearch(request);
            await CacheService.SetAsync(cacheKey, torrents, TimeSpan.FromMinutes(_config.Cache.Expiry));
            return torrents;
        }

        return await CacheService.GetOrCreateAsync(cacheKey, async () =>
        {
            var torrents = await ExecuteUnifiedSearch(request);
            return (IReadOnlyCollection<TorrentDetails>)torrents;
        }, TimeSpan.FromMinutes(_config.Cache.Expiry));
    }

    // satisfy interface for legacy v2 (can be empty or minimal)
    public async Task<RootObject> SearchJackettAsync(TorrentSearchQuery request)
    {
        return new RootObject { Results = new List<Result>(), Error = "Jackett API is disabled" };
    }

    private async Task<List<TorrentDetails>> ExecuteUnifiedSearch(TorrentSearchQuery request)
    {
        List<TorrentDetails> torrents = new();

        // 1. Try Local Search by TmdbId first
        if (request.TmdbId.HasValue)
        {
            torrents = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);
        }

        // 2. If no results or ForceSearch, do Remote Search
        if (torrents.Count == 0 || request.ForceSearch)
        {
            var (search, altname) = await _mediaResolver.ResolveKpImdb(request.Title, request.TitleOriginal);
            var trackerQuery = StringConvert.ClearTitle($"{search} {altname}".Trim());

            if (!string.IsNullOrWhiteSpace(trackerQuery))
            {
                var fetched = await _remoteSearch.SearchAsync(trackerQuery, _remoteSearch.GetSupportedTrackers());
                
                // Tag with TmdbId before saving
                foreach (var t in fetched)
                {
                    t.TmdbId = request.TmdbId;
                }

                await _repository.AddOrUpdateAsync(fetched);

                // Re-fetch from local to get structured data
                if (request.TmdbId.HasValue)
                    torrents = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);
                else
                    torrents = await _localSearch.SearchByQueryAsync(trackerQuery);
            }
        }

        var merged = await _merger.MergeAsync(torrents);
        
        var filtered = ApplyFilters(merged, request.Type, request.Tracker, 
            request.IsSerial == 2 ? 0 : request.Year, 
            request.Quality,
            request.VideoType, request.Voice, request.Season);
        
        return ApplySort(filtered, request.Sort).ToList();
    }
}
