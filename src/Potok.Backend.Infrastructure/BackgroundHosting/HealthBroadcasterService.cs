using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Potok.Backend.Infrastructure.BackgroundHosting;

public class HealthBroadcasterService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.CompletedTask;
    }
}
