using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

/// <summary>
/// Remembers which hosts currently require the FlareSolverr browser path.
/// </summary>
public sealed class CloudflareGuard
{
    private readonly ConcurrentDictionary<string, GuardState> _guarded = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<Config> _config;
    private readonly ILogger<CloudflareGuard> _logger;
    private readonly Func<DateTime> _utcNow;

    public CloudflareGuard(IOptionsMonitor<Config> config, ILogger<CloudflareGuard> logger)
        : this(config, logger, static () => DateTime.UtcNow)
    {
    }

    internal CloudflareGuard(IOptionsMonitor<Config> config, ILogger<CloudflareGuard> logger, Func<DateTime> utcNow)
    {
        _config = config;
        _logger = logger;
        _utcNow = utcNow;
    }

    public bool IsGuarded(string? host)
    {
        var settings = _config.CurrentValue.FlareSolverr;
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(host))
            return false;

        if (!_guarded.TryGetValue(host, out var state))
            return false;

        var now = _utcNow();

        if (now > state.Since.AddHours(settings.GuardedHours))
        {
            _guarded.TryRemove(host, out _);
            return false;
        }

        return true;
    }

    public void MarkGuarded(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;

        var now = _utcNow();
        _guarded.AddOrUpdate(
            host,
            _ =>
            {
                _logger.LogInformation("{Host} is behind a Cloudflare challenge; using the browser", host);
                return new GuardState { Since = now };
            },
            (_, state) =>
            {
                state.Since = now;
                return state;
            });
    }

    public void Unguard(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;

        if (_guarded.TryRemove(host, out _))
            _logger.LogInformation("{Host} answers a normal client; browser path no longer needed", host);
    }

    private sealed class GuardState
    {
        public DateTime Since;
    }
}
