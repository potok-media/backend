using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Persistence.Repositories;
using Potok.Backend.Infrastructure.SearchEngine.Services;
using Potok.Backend.Infrastructure.SearchEngine.Services.Search;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.Aniliberty;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.AnimeLayer;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.Kinozal;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.MegaPeer;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.NNMClub;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTor;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;

namespace Potok.Backend.Infrastructure.Configuration;

public static class SearchEngineServiceExtensions
{
    public static IServiceCollection AddSearchEngineServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.Configure<Config>(configuration);

        services
            .AddScoped<ITorrentRepository, TorrentRepository>()
            .AddScoped<IQueriesRepository, QueriesRepository>()
            .AddScoped<ISubscriptionRepository, SubscriptionRepository>()
            .AddScoped<ISeasonOverrideRepository, SeasonOverrideRepository>()
            .AddScoped<ITorrentEnricher, TorrentEnricher>()
            .AddScoped<ILocalSearchService, LocalSearchService>()
            .AddScoped<ITorrentMediaProbeService, TorrentMediaProbeService>()
            .AddScoped<IRemoteSearchService, RemoteSearchService>()
            .AddScoped<ISearchService, SearchService>()
            .AddScoped<ITorrentMergerService, TorrentMergerService>()
            .AddScoped<IMediaResolverService, MediaResolverService>()
            .AddScoped<ISubscribeService, SubscribeService>()
            .AddScoped<ITrackerSearch, RuTrackerSearch>()
            .AddScoped<ITrackerSearch, AnilibertySearch>()
            .AddScoped<ITrackerSearch, RuTorSearch>()
            .AddScoped<ITrackerSearch, AnimeLayerSearch>()
            .AddScoped<ITrackerSearch, NNMClubSearch>()
            .AddScoped<ITrackerSearch, KinozalSearch>()
            .AddScoped<ITrackerSearch, MegaPeerSearch>()
            .AddScoped<ITrackerRefreshProvider, RuTrackerPopularService>();

        services.AddSingleton<ICacheService, CacheService>();
        services.AddScoped<TrackerHttpClient>();
        services.AddSearchEngineHttpClients();

        return services;
    }
}