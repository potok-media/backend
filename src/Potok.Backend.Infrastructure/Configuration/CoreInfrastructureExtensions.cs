using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Potok.Backend.Infrastructure.Configuration;

public static class CoreInfrastructureExtensions
{
    public static IServiceCollection AddCoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddSingleton(connectionString);
        services.AddMemoryCache();

        return services;
    }
}