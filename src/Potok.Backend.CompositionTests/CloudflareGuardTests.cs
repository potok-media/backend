using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.CompositionTests;

public class CloudflareGuardTests
{
    [Fact]
    public void IsGuarded_NotConfigured_IsFalse()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(new FlareSolverrSettings { Enable = false }, () => now);

        guard.MarkGuarded("rutracker.org");
        Assert.False(guard.IsGuarded("rutracker.org"));
    }

    [Fact]
    public void MarkGuarded_ThenIsGuarded_IsTrue()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);

        guard.MarkGuarded("rutracker.org");
        Assert.True(guard.IsGuarded("rutracker.org"));
        Assert.True(guard.IsGuarded("RuTracker.ORG"));
    }

    [Fact]
    public void Unguard_ClearsHost()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);

        guard.MarkGuarded("rutracker.org");
        guard.Unguard("rutracker.org");
        Assert.False(guard.IsGuarded("rutracker.org"));
    }

    [Fact]
    public void IsGuarded_ExpiredTtl_IsFalse()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var settings = EnabledSettings();
        settings.GuardedHours = 1;
        var guard = CreateGuard(settings, () => now);

        guard.MarkGuarded("rutracker.org");
        now = now.AddHours(2);
        Assert.False(guard.IsGuarded("rutracker.org"));
    }

    [Fact]
    public void IsGuarded_AfterRecheckWindow_StaysOnBrowser()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var settings = EnabledSettings();
        settings.RecheckMinutes = 30;
        var guard = CreateGuard(settings, () => now);

        guard.MarkGuarded("rutracker.org");
        now = now.AddMinutes(31);
        Assert.True(guard.IsGuarded("rutracker.org"));
    }

    [Fact]
    public void IsGuarded_EmptyHost_IsFalse()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);
        Assert.False(guard.IsGuarded(null));
        Assert.False(guard.IsGuarded(""));
    }

    private static FlareSolverrSettings EnabledSettings()
    {
        return new FlareSolverrSettings
        {
            Enable = true,
            Url = "http://127.0.0.1:8191/v1",
            GuardedHours = 6,
            RecheckMinutes = 30
        };
    }

    private static CloudflareGuard CreateGuard(FlareSolverrSettings settings, Func<DateTime> utcNow)
    {
        var config = new Config { FlareSolverr = settings };
        return new CloudflareGuard(
            new StaticOptionsMonitor<Config>(config),
            NullLogger<CloudflareGuard>.Instance,
            utcNow);
    }
}

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T currentValue)
    {
        CurrentValue = currentValue;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
