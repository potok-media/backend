using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.Infrastructure.BackgroundHosting.Cloudflare;

public sealed class CloudflareWarmupHostedService : BackgroundService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<Config> _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFlareSolverrClient _flareSolverr;
    private readonly CloudflareGuard _guard;
    private readonly ILogger<CloudflareWarmupHostedService> _logger;

    public CloudflareWarmupHostedService(
        IOptionsMonitor<Config> config,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IFlareSolverrClient flareSolverr,
        CloudflareGuard guard,
        ILogger<CloudflareWarmupHostedService> logger)
    {
        _config = config;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _flareSolverr = flareSolverr;
        _guard = guard;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WarmAllAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Max(1, _config.CurrentValue.FlareSolverr.RecheckMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await WarmAllAsync(stoppingToken);
        }
    }

    private async Task WarmAllAsync(CancellationToken ct)
    {
        if (!_config.CurrentValue.FlareSolverr.IsConfigured)
            return;

        IReadOnlyList<string> urls;
        using (var scope = _scopeFactory.CreateScope())
        {
            var trackers = scope.ServiceProvider.GetRequiredService<IEnumerable<ITrackerSearch>>();
            urls = CloudflareWarmup.ProbeUrls(trackers, _config.CurrentValue);
        }

        _logger.LogInformation("Cloudflare warmup: {Count} tracker origin(s)", urls.Count);

        if (!await EnsureSessionWithRetryAsync(ct))
            _logger.LogWarning("FlareSolverr session was not ready before warmup probes");

        using var limit = new SemaphoreSlim(3, 3);
        await Task.WhenAll(urls.Select(url => WarmUrlLimitedAsync(url, limit, ct)));
    }

    private async Task WarmUrlLimitedAsync(string url, SemaphoreSlim limit, CancellationToken ct)
    {
        await limit.WaitAsync(ct);
        try
        {
            await WarmUrlAsync(url, ct);
        }
        finally
        {
            limit.Release();
        }
    }

    private async Task<bool> EnsureSessionWithRetryAsync(CancellationToken ct)
    {
        const int maxAttempts = 12;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await _flareSolverr.EnsureSessionAsync(ct))
            {
                if (attempt > 1)
                    _logger.LogInformation("FlareSolverr session ready after {Attempts} attempt(s)", attempt);
                return true;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(15, 2 * attempt));
            _logger.LogWarning(
                "FlareSolverr not ready (attempt {Attempt}/{Max}), retry in {Delay}s",
                attempt,
                maxAttempts,
                (int)delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private async Task WarmUrlAsync(string url, CancellationToken ct)
    {
        var host = new Uri(url).Host;

        try
        {
            if (!await NeedsBrowserAsync(url, ct))
            {
                _guard.Unguard(host);
                return;
            }

            _guard.MarkGuarded(host);
            var ok = await _flareSolverr.WarmupAsync(url, ct);
            if (ok)
                _logger.LogInformation("FlareSolverr warmup ok for {Host}", host);
            else
                _logger.LogWarning("FlareSolverr warmup failed for {Host}; staying on the browser path", host);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlareSolverr warmup threw for {Host}", host);
        }
    }

    private async Task<bool> NeedsBrowserAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Default");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (CloudflareChallenge.IsChallenge(response))
                return true;

            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read warmup body from {Url}", url);
            }

            if (CloudflareChallenge.IsChallengeBody(body))
                return true;

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                return true;

            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cheap GET hung — typical of a JS challenge. Try the browser.
            return true;
        }
        catch (HttpRequestException)
        {
            return true;
        }
    }
}
