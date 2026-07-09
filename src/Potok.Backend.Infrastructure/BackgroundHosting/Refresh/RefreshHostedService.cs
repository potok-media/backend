using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Options;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Infrastructure.BackgroundHosting.Refresh;

public class RefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Config _config;
    private readonly ILogger _logger;

    public RefreshHostedService(IServiceScopeFactory scopeFactory, IOptions<Config> config, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!_config.Refresh.Enable)
            return;
        
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Refresh.TimeOut));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IQueriesRepository>();
                var remoteSearch = scope.ServiceProvider.GetRequiredService<IRemoteSearchService>();

                var queries = await repository.GetStaleSearchQueriesAsync(TimeSpan.FromMinutes(_config.Refresh.OlderThanMin), _config.Refresh.Limit);
                if (queries.Count == 0) continue;

                _logger.Information("Update queries {@Queries}", (object)queries.Select(x => new
                {
                    query = x.Query,
                    tmdb_id = x.TmdbId
                }));
                foreach (var query in queries)
                {
                    await remoteSearch.SearchAsync(query.Query);
                    await repository.UpdateLastRefreshTimeAsync(query.TmdbId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RefreshHostedFailed failed");
            }
        }
    }
}