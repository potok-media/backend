using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.RuTracker;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Infrastructure.BackgroundHosting.RuTracker;

public class RuTrackerPopularHostedService : BackgroundService
{
    private readonly Config _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public RuTrackerPopularHostedService(IServiceScopeFactory scopeFactory, IOptions<Config> config, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.RuTracker.Popular.TimeOut));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<ITrackerRefreshProvider>>();
                var ruTrackerPopularService =
                    providers.FirstOrDefault(x => x is RuTrackerPopularService) as RuTrackerPopularService ??
                    throw new ArgumentException(nameof(providers));
                
                _logger.Information("RuTracker popular sync started. Categories: '{@Categories}' ids", (object)_config.RuTracker.Popular.Categories);
                await ruTrackerPopularService.InvokeAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RuTrackerPopularHostedService failed");
            }
    }
}