using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

public sealed class FlareSolverrClient : IFlareSolverrClient, IDisposable
{
    public const string HttpClientName = "FlareSolverr";
    public const string SessionPrefix = "potok_";

    private const int SessionCreateTimeoutMs = 60_000;
    private const int PingTimeoutMs = 15_000;
    private const int ChallengeAttemptTimeoutMs = 40_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<Config> _config;
    private readonly TrackerProxyPool _pool;
    private readonly ILogger<FlareSolverrClient> _logger;
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _idleTimer;
    private bool _disposed;

    public FlareSolverrClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<Config> config,
        TrackerProxyPool pool,
        ILogger<FlareSolverrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _pool = pool;
        _logger = logger;
    }

    public Task<FlareSolverrSolution?> GetAsync(
        string url,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        return ExecuteAsync("request.get", url, postData: null, cookieHeader, proxy, ct);
    }

    public Task<FlareSolverrSolution?> PostAsync(
        string url,
        string? postData,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        return ExecuteAsync("request.post", url, postData, cookieHeader, proxy, ct);
    }

    public async Task<bool> WarmupAsync(string url, CancellationToken ct)
    {
        var solution = await GetAsync(url, cookieHeader: null, proxy: null, ct);
        return solution is not null && !string.IsNullOrWhiteSpace(solution.Html);
    }

    public async Task<bool> EnsureSessionAsync(CancellationToken ct)
    {
        var settings = _config.CurrentValue.FlareSolverr;
        if (!settings.IsConfigured)
            return false;

        var root = await CallAsync(
            settings,
            new FlareSolverrRequest { Cmd = "sessions.list" },
            PingTimeoutMs,
            ct);

        return root is not null &&
               (string.Equals(root.Status, "ok", StringComparison.OrdinalIgnoreCase)
                || root.Sessions is not null);
    }

    internal static string SessionIdFor(string host)
    {
        var chars = host.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        return SessionPrefix + new string(chars);
    }

    private async Task<FlareSolverrSolution?> ExecuteAsync(
        string cmd,
        string url,
        string? postData,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        var settings = _config.CurrentValue.FlareSolverr;
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(url))
            return null;

        string host;
        try
        {
            host = new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return null;
        }

        var session = _sessions.GetOrAdd(host, static h => new HostSession(SessionIdFor(h)));
        await session.Gate.WaitAsync(ct);
        try
        {
            var egress = proxy ?? session.Proxy ?? ToFlare(_pool.Next());
            if (proxy is null)
                session.Proxy = egress;

            if (!session.Alive && !await CreateSessionAsync(settings, session, egress, CancellationToken.None))
                return null;

            if (ct.IsCancellationRequested)
                return null;

            var (outcome, solution) = await RequestAsync(settings, session, cmd, url, postData, cookieHeader, egress, ct);

            if (outcome == FetchOutcome.BrowserFailed)
            {
                await DestroySessionAsync(settings, session, CancellationToken.None);
                if (ct.IsCancellationRequested)
                    return null;

                egress = proxy ?? ToFlare(_pool.Next());
                if (proxy is null)
                    session.Proxy = egress;

                if (!await CreateSessionAsync(settings, session, egress, CancellationToken.None))
                    return null;
                if (ct.IsCancellationRequested)
                    return null;

                (outcome, solution) = await RequestAsync(settings, session, cmd, url, postData, cookieHeader, egress, ct);
                if (outcome == FetchOutcome.Ok)
                    _logger.LogWarning("{Host}: FlareSolverr succeeded after recreating the session", host);
            }

            session.LastUse = DateTime.UtcNow;
            ArmIdleTimer(settings);
            return solution;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlareSolverr request failed for {Host}", host);
            return null;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private enum FetchOutcome
    {
        Ok,
        BrowserFailed
    }

    private async Task<(FetchOutcome Outcome, FlareSolverrSolution? Solution)> RequestAsync(
        FlareSolverrSettings settings,
        HostSession session,
        string cmd,
        string url,
        string? postData,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        var requestCookies = FlareSolverrCookieParser.ParseHeader(cookieHeader);
        var payload = new FlareSolverrRequest
        {
            Cmd = cmd,
            Session = session.Id,
            Url = url,
            MaxTimeout = Math.Min(settings.MaxTimeoutMs, ChallengeAttemptTimeoutMs),
            PostData = cmd == "request.post" ? postData ?? string.Empty : postData,
            Cookies = requestCookies.Count > 0 ? requestCookies : null,
            Proxy = ToProxyDto(proxy)
        };

        var root = await CallAsync(settings, payload, ChallengeAttemptTimeoutMs + 5_000, ct);
        if (root is null)
            return (FetchOutcome.BrowserFailed, null);

        if (!string.Equals(root.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.Message ?? "";
            _logger.LogError("FlareSolverr refused: {Message}", ShortFlareMessage(message));
            if (IsDeadSession(message))
                session.Alive = false;

            return (FetchOutcome.BrowserFailed, null);
        }

        var solution = root.Solution;
        var cookies = FlareSolverrCookieParser.ToCookies(solution?.Cookies);
        return (FetchOutcome.Ok, new FlareSolverrSolution(solution?.Status ?? 0, solution?.Response, cookies));
    }

    private async Task<bool> CreateSessionAsync(
        FlareSolverrSettings settings,
        HostSession session,
        FlareSolverrProxy? proxy,
        CancellationToken ct)
    {
        var root = await CallAsync(
            settings,
            new FlareSolverrRequest
            {
                Cmd = "sessions.create",
                Session = session.Id,
                Proxy = ToProxyDto(proxy)
            },
            SessionCreateTimeoutMs,
            ct);

        var ok = root is not null &&
                 (string.Equals(root.Status, "ok", StringComparison.OrdinalIgnoreCase)
                  || (root.Message ?? "").Contains("already exists", StringComparison.OrdinalIgnoreCase));

        session.Alive = ok;
        if (ok)
        {
            if (proxy is not null)
                _logger.LogInformation("FlareSolverr session {Session} created via {Proxy}", session.Id, proxy.Url);
            else
                _logger.LogInformation("FlareSolverr session {Session} created", session.Id);
        }
        else
        {
            session.Proxy = null;
            _logger.LogWarning(
                "FlareSolverr session {Session} create failed: {Message}",
                session.Id,
                root?.Message ?? "no response");
        }

        return ok;
    }

    private async Task DestroySessionAsync(FlareSolverrSettings settings, HostSession session, CancellationToken ct)
    {
        try
        {
            if (session.Alive)
            {
                await CallAsync(
                    settings,
                    new FlareSolverrRequest { Cmd = "sessions.destroy", Session = session.Id },
                    60_000,
                    ct);
            }
        }
        finally
        {
            session.Alive = false;
            session.Proxy = null;
        }
    }

    private void ArmIdleTimer(FlareSolverrSettings settings)
    {
        if (settings.SessionIdleMinutes <= 0)
            return;

        _idleTimer ??= new Timer(_ => CloseIdleSessions(), null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void CloseIdleSessions()
    {
        var settings = _config.CurrentValue.FlareSolverr;
        if (!settings.IsConfigured || settings.SessionIdleMinutes <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddMinutes(-settings.SessionIdleMinutes);
        foreach (var session in _sessions.Values)
        {
            if (!session.Alive || session.LastUse > cutoff)
                continue;
            if (!session.Gate.Wait(0))
                continue;

            _ = CloseIdleSessionAsync(settings, session);
        }
    }

    private async Task CloseIdleSessionAsync(FlareSolverrSettings settings, HostSession session)
    {
        try
        {
            await DestroySessionAsync(settings, session, CancellationToken.None);
            _logger.LogInformation("FlareSolverr session {Session} closed after idle timeout", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close idle FlareSolverr session {Session}", session.Id);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<FlareSolverrApiResponse?> CallAsync(
        FlareSolverrSettings settings,
        FlareSolverrRequest payload,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(settings.Url, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return JsonSerializer.Deserialize<FlareSolverrApiResponse>(body, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("FlareSolverr timed out after {Timeout}ms at {Url}", timeoutMs, settings.Url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlareSolverr is unreachable at {Url}", settings.Url);
            return null;
        }
    }

    private static FlareSolverrProxy? ToFlare(ProxyEndpoint? item)
    {
        if (item is null)
            return null;

        // Chromium cannot authenticate SOCKS. Residential providers usually speak HTTP
        // on the same host:port, and FlareSolverr/Chrome can do HTTP 407 auth.
        var url = item.Url;
        if (url.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            url = "http://" + rest;
        }

        return new FlareSolverrProxy(url, item.Username, item.Password);
    }

    public static string ShortFlareMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var cut = message.IndexOf('\n');
        if (cut > 0)
            message = message[..cut];

        var sessionInfo = message.IndexOf(" (Session info", StringComparison.Ordinal);
        if (sessionInfo > 0)
            message = message[..sessionInfo];

        return message.Trim();
    }

    private static bool IsDeadSession(string message)
    {
        return message.Contains("session", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || message.Contains("ERR_SOCKS", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static FlareSolverrProxyDto? ToProxyDto(FlareSolverrProxy? proxy)
    {
        if (proxy is null || string.IsNullOrWhiteSpace(proxy.Url))
            return null;

        return new FlareSolverrProxyDto
        {
            Url = proxy.Url,
            Username = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
            Password = string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _idleTimer?.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
    }

    private sealed class HostSession : IDisposable
    {
        public HostSession(string id) => Id = id;

        public string Id { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Alive;
        public DateTime LastUse = DateTime.MinValue;
        public FlareSolverrProxy? Proxy;

        public void Dispose() => Gate.Dispose();
    }
}
