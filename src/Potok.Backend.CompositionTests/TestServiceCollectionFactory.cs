using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Infrastructure.Configuration;
using Serilog;

namespace Potok.Backend.CompositionTests;

internal static class TestServiceCollectionFactory
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=potok_test;Username=potok;Password=potok";

    internal static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                ["Gateway:TmdbApiKey"] = "test-key",
                ["Gateway:JwtSecret"] = "test-jwt-secret-key-32-chars-minimum",
            })
            .Build();
    }

    internal static ServiceProvider BuildGatewayDomainProvider()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        services.AddSingleton(Log.Logger);
        services.Configure<GatewayOptions>(configuration.GetSection("Gateway"));
        services.AddHttpContextAccessor();
        services.AddCoreInfrastructure(configuration);
        services.AddGatewayServices(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    internal static ServiceProvider BuildSearchEngineDomainProvider()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        services.AddSingleton(Log.Logger);
        services.AddCoreInfrastructure(configuration);
        services.AddSearchEngineServices(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}