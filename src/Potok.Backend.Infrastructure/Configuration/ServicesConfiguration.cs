using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;
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

        // Register Telegram auth only when configured (bot token + username present). When disabled,
        // TelegramAuthController receives a null service and reports the feature as unavailable.
        var gatewayOptions = configuration.GetSection("Gateway").Get<GatewayOptions>();
        if (gatewayOptions?.TelegramAuthEnabled == true)
        {
            services.AddScoped<ITelegramAuthService, TelegramAuthService>();
            // Deep-link (bot) flow for HTTP/LAN deployments: a shared code store plus a background
            // long-poller that receives /start <code> messages from the Telegram bot.
            services.AddSingleton<ITelegramLinkCodeStore, TelegramLinkCodeStore>();
            services.AddHostedService<TelegramBotPollingService>();
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddGatewayMigrations(connectionString);

        return services;
    }
}