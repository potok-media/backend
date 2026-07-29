using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.Caching;
using Potok.Backend.Infrastructure.Gateway.Services;
using Potok.Backend.Infrastructure.Persistence.Repositories;

namespace Potok.Backend.Infrastructure.Configuration;

public static class GatewayServiceExtensions
{
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatewayOptions>(configuration.GetSection("Gateway"));
        services.Configure<CacheOptions>(options => options.Enable = true);

        services
            .AddScoped<IUserRepository, UserRepository>()
            .AddScoped<IUserHistoryRepository, UserHistoryRepository>()
            .AddScoped<IUserListsRepository, UserListsRepository>()
            .AddScoped<IHomeService, HomeService>()
            .AddScoped<IMediaOrchestrator, MediaOrchestrator>()
            .AddScoped<ILibraryOrchestrator, LibraryOrchestrator>()
            .AddTransient<TraktApiHandler>();

        services.AddSingleton<ICacheService, CacheService>();

        services.AddGatewayHttpClients();

        services.AddHttpClient<TmdbClient>(client => { client.BaseAddress = new Uri("https://api.themoviedb.org/3/"); })
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            });

        services.AddHttpClient<TraktClient>()
            .AddHttpMessageHandler<TraktApiHandler>()
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            });

        services.AddHttpClient("TraktProxy")
            .AddHttpMessageHandler<TraktApiHandler>()
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            });

        return services;
    }
}