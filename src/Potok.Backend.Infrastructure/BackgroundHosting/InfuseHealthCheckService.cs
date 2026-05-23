using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Infrastructure.BackgroundHosting;

public class InfuseHealthCheckService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InfuseHealthCheckService> _logger;

    public InfuseHealthCheckService(IServiceProvider serviceProvider, ILogger<InfuseHealthCheckService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit after startup
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCheckAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }

    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInfuseRepository>();
            var trackerSearch = scope.ServiceProvider.GetRequiredService<ITrackerSearch>();

            _logger.LogInformation("Starting Infuse health check...");

            var items = await repo.GetAllAsync();
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (item.Status != InfuseItemStatus.Active || string.IsNullOrEmpty(item.Link))
                    continue;

                _logger.LogDebug("Checking health for Infuse item {Title} ({Link})", item.Title, item.Link);
                
                // Note: Actual verification depends on ITrackerSearch implementation details.
                // For now, this is a placeholder for the logic that would fetch the tracker page
                // and compare seeders/hash.
            }
            
            _logger.LogInformation("Infuse health check completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running Infuse health check");
        }
    }
}
