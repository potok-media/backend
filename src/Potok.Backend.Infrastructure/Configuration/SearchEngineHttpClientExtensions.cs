using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.Infrastructure.Configuration;

public static class SearchEngineHttpClientExtensions
{
    private static readonly TimeSpan TrackerRequestTimeout = TimeSpan.FromSeconds(20);

    public static IServiceCollection AddSearchEngineHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient("Default", ConfigureTrackerClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateTrackerHandler(sp, allowAutoRedirect: true, useProxy: true));

        services.AddHttpClient("DefaultNoRedirect", ConfigureTrackerClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateTrackerHandler(sp, allowAutoRedirect: false, useProxy: true));

        services.AddHttpClient("NoProxy", ConfigureTrackerClient)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler());

        services.AddHttpClient("NoProxyNoRedirect", ConfigureTrackerClient)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler(allowAutoRedirect: false));

        services.AddHttpClient(FlareSolverrClient.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        return services;
    }

    private static void ConfigureTrackerClient(HttpClient client)
    {
        HttpClientSetup.ApplyBrowserHeaders(client);
        client.Timeout = TrackerRequestTimeout;
    }

    private static HttpClientHandler CreateTrackerHandler(
        IServiceProvider serviceProvider,
        bool allowAutoRedirect,
        bool useProxy)
    {
        IWebProxy? proxy = null;
        if (useProxy)
        {
            var pool = serviceProvider.GetRequiredService<TrackerProxyPool>();
            if (pool.HasProxies)
                proxy = new RotatingWebProxy(pool);
        }

        return HttpClientSetup.CreateHandler(allowAutoRedirect, proxy);
    }
}