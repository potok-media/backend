using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.BackgroundHosting.Media;
using Potok.Backend.Infrastructure.BackgroundHosting.Refresh;
using Potok.Backend.Infrastructure.BackgroundHosting.RuTracker;
using Potok.Backend.Infrastructure.Gateway.Security;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Configuration;

public static class ServicesConfiguration
{
    public static IServiceCollection AddGatewayInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddResponseCaching();
        services.AddExceptionHandler<Middlewares.GlobalExceptionHandler>();
        services.AddProblemDetails();

        services.AddControllers();
        services.AddOpenApi();
        services.AddSwaggerGen();
        services.AddHttpClient();

        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IEventBroadcaster, Gateway.Services.EventBroadcaster>();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddGatewayMigrations(connectionString);

        return services;
    }

    public static IServiceCollection AddSearchEngineInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddSearchEngineMigrations(connectionString);

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

        return services;
    }
}