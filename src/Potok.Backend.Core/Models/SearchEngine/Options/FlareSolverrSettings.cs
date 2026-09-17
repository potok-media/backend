using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

/// <summary>
///     Cloudflare bypass via FlareSolverr.
///     Cookie <c>cf_clearance</c> cannot be reused in HttpClient (TLS fingerprint);
///     guarded hosts must be fetched entirely through the browser session.
/// </summary>
public class FlareSolverrSettings
{
    [ConfigurationKeyName("enable")]
    public bool Enable { get; set; } = false;

    [ConfigurationKeyName("url")]
    public string Url { get; set; } = "http://flaresolverr:8191/v1";

    [ConfigurationKeyName("max-timeout-ms")]
    public int MaxTimeoutMs { get; set; } = 180_000;

    [ConfigurationKeyName("session-idle-minutes")]
    public int SessionIdleMinutes { get; set; } = 30;

    [ConfigurationKeyName("guarded-hours")]
    public int GuardedHours { get; set; } = 6;

    [ConfigurationKeyName("recheck-minutes")]
    public int RecheckMinutes { get; set; } = 30;

    public bool IsConfigured => Enable && !string.IsNullOrWhiteSpace(Url);
}
