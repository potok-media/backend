using System.Net;
using System.Security.Authentication;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.BackgroundHosting;
using Potok.Backend.Infrastructure.BackgroundHosting.Media;
using Potok.Backend.Infrastructure.BackgroundHosting.Refresh;
using Potok.Backend.Infrastructure.BackgroundHosting.RuTracker;
using Potok.Backend.Infrastructure.Gateway.Services;
using Potok.Backend.Infrastructure.Middlewares;
using Potok.Backend.Infrastructure.Migrations.Configurations;
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

public static class ServicesConfiguration
{
    public static IServiceCollection AddSharedInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddScoped<ITorrentRepository, TorrentRepository>()
            .AddScoped<IQueriesRepository, QueriesRepository>()
            .AddScoped<ISubscriptionRepository, SubscriptionRepository>()
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
            .AddScoped<ITrackerRefreshProvider, RuTrackerPopularService>()
            .AddScoped<IHomeService, HomeService>()
            .AddScoped<IMediaOrchestrator, MediaOrchestrator>()
            .AddScoped<ILibraryOrchestrator, LibraryOrchestrator>()
            .AddScoped<IUserRepository, UserRepository>()
            .AddScoped<IUserHistoryRepository, UserHistoryRepository>()
            .AddScoped<IUserListsRepository, UserListsRepository>()
            .AddTransient<TraktApiHandler>();

        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IEventBroadcaster, EventBroadcaster>();
        services.AddMemoryCache();
        
        services.AddScoped<Potok.Backend.Infrastructure.Http.TrackerHttpClient>();

        Action<HttpClient> configureClient = client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Potok.Backend.Infrastructure.Http.TrackerHttpClient.DefaultUserAgent);
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        };

        services.AddHttpClient("Default", configureClient)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var config = sp.GetRequiredService<IOptionsMonitor<Config>>().CurrentValue;
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    CheckCertificateRevocationList = false,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };

                if (config.Proxy?.List?.Count > 0)
                {
                    var proxyItem = config.Proxy.List[Random.Shared.Next(config.Proxy.List.Count)];
                    var proxy = new WebProxy(proxyItem.Url);

                    if (!string.IsNullOrEmpty(proxyItem.Username))
                        proxy.Credentials = new NetworkCredential(proxyItem.Username, proxyItem.Password);

                    proxy.BypassProxyOnLocal = config.Proxy.BypassOnLocal;
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }

                return handler;
            });

        services.AddHttpClient("DefaultNoRedirect", configureClient)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var config = sp.GetRequiredService<IOptionsMonitor<Config>>().CurrentValue;
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    CheckCertificateRevocationList = false,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowAutoRedirect = false
                };

                if (config.Proxy?.List?.Count > 0)
                {
                    var proxyItem = config.Proxy.List[Random.Shared.Next(config.Proxy.List.Count)];
                    var proxy = new WebProxy(proxyItem.Url);

                    if (!string.IsNullOrEmpty(proxyItem.Username))
                        proxy.Credentials = new NetworkCredential(proxyItem.Username, proxyItem.Password);

                    proxy.BypassProxyOnLocal = config.Proxy.BypassOnLocal;
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }

                return handler;
            });

        services.AddHttpClient("NoProxy", configureClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                CheckCertificateRevocationList = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            });

        services.AddHttpClient("NoProxyNoRedirect", configureClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                CheckCertificateRevocationList = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowAutoRedirect = false
            });
        
        services.AddHttpClient<TmdbClient>(client => { client.BaseAddress = new Uri("https://api.themoviedb.org/3/"); }).AddStandardResilienceHandler();
        services.AddHttpClient<TraktClient>().AddHttpMessageHandler<TraktApiHandler>().AddStandardResilienceHandler();
        services.AddHttpClient("TraktProxy").AddHttpMessageHandler<TraktApiHandler>().AddStandardResilienceHandler();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddSingleton(connectionString);
        
        return services;
    }

    public static IServiceCollection AddGatewayInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddResponseCaching();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        services.Configure<GatewayOptions>(configuration.GetSection("Gateway"));
        services.AddControllers();
        services.AddOpenApi();
        services.AddSwaggerGen();

        services.AddHttpClient();

        services.AddScoped<IPasswordHasher, Potok.Backend.Infrastructure.Gateway.Security.PasswordHasher>();
        services.AddScoped<IJwtTokenService, Potok.Backend.Infrastructure.Gateway.Security.JwtTokenService>();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddGatewayMigrations(connectionString);

        return services;
    }

    public static IServiceCollection AddSearchEngineInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddSearchEngineMigrations(connectionString);

        services.Configure<Config>(configuration);

        services.AddEndpointsApiExplorer();
        services.AddOpenApi();
        services.AddControllers();

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .SelectMany(e => e.Value!.Errors)
                    .Select(e => e.ErrorMessage)
                    .Distinct()
                    .ToArray();

                return new BadRequestObjectResult(new
                {
                    error = "Validation failed",
                    details = errors
                });
            };
        });

        services.AddResponseCompression(options =>
        {
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes
                .Concat(["application/vnd.apple.mpegurl", "image/svg+xml"]);
        });

        services.AddRouting(options => options.LowercaseUrls = true);
        
        services.AddHostedService<TorrentMediaProbeHostedService>();
        services.AddHostedService<RuTrackerPopularHostedService>();
        services.AddHostedService<RefreshHostedService>();
        services.AddHostedService<HealthBroadcasterService>();

        return services;
    }
}
