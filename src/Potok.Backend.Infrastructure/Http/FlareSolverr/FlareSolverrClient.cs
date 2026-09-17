using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

public sealed class FlareSolverrClient : IFlareSolverrClient, IDisposable
{
    public const string HttpClientName = "FlareSolverr";
    public const string SessionName = "potok";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<Config> _config;
    private readonly ILogger<FlareSolverrClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _sessionAlive;
    private DateTime _lastUse = DateTime.MinValue;
    private Timer? _idleTimer;
    private bool _disposed;

    public FlareSolverrClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<Config> config,
        ILogger<FlareSolverrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
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

        await _gate.WaitAsync(ct);
        try
        {
            if (!_sessionAlive && !await CreateSessionAsync(settings, ct))
                return null;

            var (outcome, solution) = await RequestAsync(settings, cmd, url, postData, cookieHeader, proxy, ct);

            if (outcome == FetchOutcome.BrowserFailed)
            {
                await DestroySessionAsync(settings, ct);
                if (!await CreateSessionAsync(settings, ct))
                    return null;

                (outcome, solution) = await RequestAsync(settings, cmd, url, postData, cookieHeader, proxy, ct);
                if (outcome == FetchOutcome.Ok)
                    _logger.LogWarning("{Host}: FlareSolverr succeeded after recreating the session", host);
            }

            _lastUse = DateTime.UtcNow;
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
            _gate.Release();
        }
    }

    private enum FetchOutcome
    {
        Ok,
        BrowserFailed
    }

    private async Task<(FetchOutcome Outcome, FlareSolverrSolution? Solution)> RequestAsync(
        FlareSolverrSettings settings,
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
            Session = SessionName,
            Url = url,
            MaxTimeout = settings.MaxTimeoutMs,
            PostData = cmd == "request.post" ? postData ?? string.Empty : postData,
            Cookies = requestCookies.Count > 0 ? requestCookies : null,
            Proxy = ToProxyDto(proxy)
        };

        var root = await CallAsync(settings, payload, settings.MaxTimeoutMs + 30_000, ct);
        if (root is null)
            return (FetchOutcome.BrowserFailed, null);

        if (!string.Equals(root.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.Message ?? "";
            _logger.LogError("FlareSolverr refused: {Message}", message);
            if (message.Contains("session", StringComparison.OrdinalIgnoreCase))
                _sessionAlive = false;

            return (FetchOutcome.BrowserFailed, null);
        }

        var solution = root.Solution;
        var cookies = FlareSolverrCookieParser.ToCookies(solution?.Cookies);
        return (FetchOutcome.Ok, new FlareSolverrSolution(solution?.Status ?? 0, solution?.Response, cookies));
    }

    private async Task<bool> CreateSessionAsync(FlareSolverrSettings settings, CancellationToken ct)
    {
        var root = await CallAsync(
            settings,
            new FlareSolverrRequest { Cmd = "sessions.create", Session = SessionName },
            settings.MaxTimeoutMs + 30_000,
            ct);

        var ok = root is not null &&
                 (string.Equals(root.Status, "ok", StringComparison.OrdinalIgnoreCase)
                  || (root.Message ?? "").Contains("already exists", StringComparison.OrdinalIgnoreCase));

        _sessionAlive = ok;
        if (ok)
            _logger.LogInformation("FlareSolverr browser session created");
        else
            _logger.LogError("FlareSolverr session create failed: {Message}", root?.Message);

        return ok;
    }

    private async Task DestroySessionAsync(FlareSolverrSettings settings, CancellationToken ct)
    {
        if (!_sessionAlive)
            return;

        await CallAsync(
            settings,
            new FlareSolverrRequest { Cmd = "sessions.destroy", Session = SessionName },
            60_000,
            ct);
        _sessionAlive = false;
    }

    private void ArmIdleTimer(FlareSolverrSettings settings)
    {
        if (settings.SessionIdleMinutes <= 0)
            return;

        _idleTimer ??= new Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        var period = TimeSpan.FromMinutes(1);
        _idleTimer.Change(period, period);
    }

    private void CloseIfIdle()
    {
        var settings = _config.CurrentValue.FlareSolverr;
        if (!settings.IsConfigured || !_sessionAlive || settings.SessionIdleMinutes <= 0)
            return;

        if (DateTime.UtcNow < _lastUse.AddMinutes(settings.SessionIdleMinutes))
            return;

        if (!_gate.Wait(0))
            return;

        _ = CloseIdleSessionAsync(settings);
    }

    private async Task CloseIdleSessionAsync(FlareSolverrSettings settings)
    {
        try
        {
            await DestroySessionAsync(settings, CancellationToken.None);
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _logger.LogInformation("FlareSolverr session closed after idle timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close idle FlareSolverr session");
        }
        finally
        {
            _gate.Release();
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("FlareSolverr timed out talking to {Url}", settings.Url);
            _sessionAlive = false;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlareSolverr is unreachable at {Url}", settings.Url);
            _sessionAlive = false;
            return null;
        }
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
        _gate.Dispose();
    }
}
