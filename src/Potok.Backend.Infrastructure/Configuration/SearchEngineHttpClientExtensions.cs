using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Models.SearchEngine.Options;

namespace Potok.Backend.Infrastructure.Configuration;

public static class SearchEngineHttpClientExtensions
{
    public static IServiceCollection AddSearchEngineHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient("Default", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateTrackerHandler(sp, allowAutoRedirect: true, useProxy: true));

        services.AddHttpClient("DefaultNoRedirect", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateTrackerHandler(sp, allowAutoRedirect: false, useProxy: true));

        services.AddHttpClient("NoProxy", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler());

        services.AddHttpClient("NoProxyNoRedirect", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler(allowAutoRedirect: false));

        return services;
    }

    private static HttpClientHandler CreateTrackerHandler(
        IServiceProvider serviceProvider,
        bool allowAutoRedirect,
        bool useProxy)
    {
        IWebProxy? proxy = null;
        if (useProxy)
        {
            var config = serviceProvider.GetRequiredService<IOptionsMonitor<Config>>().CurrentValue;
            if (config.Proxy?.List?.Count > 0)
            {
                var proxyItem = config.Proxy.List[Random.Shared.Next(config.Proxy.List.Count)];
                proxy = new WebProxy(proxyItem.Url);

                if (!string.IsNullOrEmpty(proxyItem.Username))
                {
                    proxy.Credentials = new NetworkCredential(proxyItem.Username, proxyItem.Password);
                }

                if (proxy is WebProxy webProxy)
                {
                    webProxy.BypassProxyOnLocal = config.Proxy.BypassOnLocal;
                }
            }
        }

        return HttpClientSetup.CreateHandler(allowAutoRedirect, proxy);
    }
}