using Microsoft.Extensions.DependencyInjection;

namespace Potok.Backend.Infrastructure.Configuration;

public static class GatewayHttpClientExtensions
{
    public static IServiceCollection AddGatewayHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient("GatewayProxy", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler());

        services.AddHttpClient("GatewayOutbound", HttpClientSetup.ApplyBrowserHeaders)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSetup.CreateHandler());

        return services;
    }
}