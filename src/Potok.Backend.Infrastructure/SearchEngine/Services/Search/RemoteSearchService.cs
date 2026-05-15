using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;
using Serilog;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Search;

public class RemoteSearchService : BaseSearchService, IRemoteSearchService
{
    private readonly ICacheService _cacheService;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<TrackerType, ITrackerSearch> _providers;

    private static readonly ConcurrentDictionary<TrackerType, ResiliencePipeline<IReadOnlyCollection<TorrentDetails>>> Pipelines = new();

    public RemoteSearchService(IOptions<Config> config, HttpService httpService, ICacheService cacheService, ILogger logger,
        IEnumerable<ITrackerSearch> providers) : base(config.Value, httpService, cacheService)
    {
        _cacheService = cacheService;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.Tracker, p => p);
    }

    public IReadOnlyCollection<TrackerType> GetSupportedTrackers()
    {
        return _providers.Keys.OrderBy(t => t).ToArray();
    }

    public async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query,
        IReadOnlyCollection<TrackerType>? trackers = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var targetTrackers = ResolveTrackers(trackers);
        if (targetTrackers.Count == 0)
            return [];

        return await SearchUncachedAsync(query, targetTrackers);
    }

    private IReadOnlyCollection<TrackerType> ResolveTrackers(IReadOnlyCollection<TrackerType>? trackers)
    {
        var candidates = trackers == null || trackers.Count == 0
            ? _providers.Keys
            : trackers.Where(t => _providers.ContainsKey(t));

        return candidates.Distinct().ToArray();
    }

    private async Task<IReadOnlyCollection<TorrentDetails>> SearchUncachedAsync(
        string query,
        IReadOnlyCollection<TrackerType> trackers)
    {
        var bag = new ConcurrentBag<IReadOnlyCollection<TorrentDetails>>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        _logger.Information("Search '{@Query}' on {@Trackers} trackers", query, trackers);

        await Parallel.ForEachAsync(trackers, options, async (tracker, ct) =>
        {
            var sw = new Stopwatch();
            sw.Start();
            var res = await SearchTrackerSafeAsync(tracker, query, ct);
            if (res.Count > 0)
                bag.Add(res);
            sw.Stop();
            _logger.Information("Tracker: {Tracker}; \tSW: {SW}ms", tracker, sw.ElapsedMilliseconds);
        });

        var merged = new List<TorrentDetails>();
        foreach (var list in bag)
            if (list.Count > 0)
                merged.AddRange(list);

        return merged;
    }

    private async Task<IReadOnlyCollection<TorrentDetails>> SearchTrackerSafeAsync(
        TrackerType tracker,
        string query,
        CancellationToken ct)
    {
        if (!_providers.TryGetValue(tracker, out var provider))
            return [];

        var pipeline = GetPipeline(tracker);

        try
        {
            return await pipeline.ExecuteAsync(async token => await provider.SearchAsync(query), ct);
        }
        catch (BrokenCircuitException)
        {
            _logger.Warning("Circuit is OPEN for tracker {Tracker}. Search skipped.", tracker);
        }
        catch (TimeoutRejectedException)
        {
            _logger.Warning("Search TIMEOUT (Polly) for tracker {Tracker}", tracker);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Tracker search cancelled for {Tracker}", tracker);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Tracker search failed for {Tracker}", tracker);
        }

        return [];
    }

    private ResiliencePipeline<IReadOnlyCollection<TorrentDetails>> GetPipeline(TrackerType tracker)
    {
        return Pipelines.GetOrAdd(tracker, _ => 
        {
            return new ResiliencePipelineBuilder<IReadOnlyCollection<TorrentDetails>>()
                .AddRetry(new RetryStrategyOptions<IReadOnlyCollection<TorrentDetails>>
                {
                    ShouldHandle = new PredicateBuilder<IReadOnlyCollection<TorrentDetails>>()
                        .Handle<Exception>(ex => ex is not BrokenCircuitException && ex is not OperationCanceledException),
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    OnRetry = args =>
                    {
                        _logger.Debug("Retrying search for {Tracker}. Attempt: {Attempt}", tracker, args.AttemptNumber);
                        return default;
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<IReadOnlyCollection<TorrentDetails>>
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 3,
                    BreakDuration = TimeSpan.FromMinutes(1),
                    ShouldHandle = new PredicateBuilder<IReadOnlyCollection<TorrentDetails>>().Handle<Exception>(),
                    OnOpened = _ =>
                    {
                        _logger.Error("Circuit Breaker OPENED for {Tracker} for 1 minute", tracker);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        _logger.Information("Circuit Breaker CLOSED for {Tracker}", tracker);
                        return default;
                    }
                })
                .AddTimeout(TimeSpan.FromSeconds(10))
                .Build();
        });
    }
}