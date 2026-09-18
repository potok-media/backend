using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.CompositionTests;

public class FlareSolverrClientTests
{
    [Fact]
    public async Task GetAsync_CreatesSessionThenReturnsSolution()
    {
        var handler = new QueueHandler([
            JsonOk("""{"status":"ok","message":"Session created"}"""),
            JsonOk("""
                {
                  "status": "ok",
                  "solution": {
                    "status": 200,
                    "response": "<html>forum</html>",
                    "cookies": [{"name": "cf_clearance", "value": "tok"}]
                  }
                }
                """)
        ]);

        using var client = CreateClient(handler);
        var solution = await client.GetAsync(
            "https://rutracker.org/forum/index.php",
            cookieHeader: "bb_session=1",
            proxy: null,
            CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(200, solution.Status);
        Assert.Equal("<html>forum</html>", solution.Html);
        Assert.Equal("cf_clearance", solution.Cookies[0].Name);
        Assert.Equal(2, handler.Bodies.Count);

        using var create = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("sessions.create", create.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("potok_rutracker_org", create.RootElement.GetProperty("session").GetString());
        Assert.False(create.RootElement.TryGetProperty("proxy", out _));

        using var get = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("request.get", get.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("https://rutracker.org/forum/index.php", get.RootElement.GetProperty("url").GetString());
        Assert.Equal("bb_session", get.RootElement.GetProperty("cookies")[0].GetProperty("name").GetString());
        Assert.False(get.RootElement.TryGetProperty("proxy", out _));
    }

    [Fact]
    public async Task GetAsync_AttachesStickyPoolProxyToSessionAndRequest()
    {
        var handler = new QueueHandler([
            JsonOk("""{"status":"ok","message":"Session created"}"""),
            JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>ok</html>","cookies":[]}}""")
        ]);

        var config = EnabledConfig();
        config.Proxy.List.Add("http://u:s@p1.example:8080");
        config.Proxy.List.Add("http://p2.example:8080");

        using var client = CreateClient(handler, config);
        var solution = await client.GetAsync(
            "https://rutracker.org/",
            cookieHeader: null,
            proxy: null,
            CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(2, handler.Bodies.Count);

        using var create = JsonDocument.Parse(handler.Bodies[0]);
        var createProxy = create.RootElement.GetProperty("proxy");
        Assert.Equal("http://p1.example:8080", createProxy.GetProperty("url").GetString());
        Assert.Equal("u", createProxy.GetProperty("username").GetString());

        using var get = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("http://p1.example:8080", get.RootElement.GetProperty("proxy").GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetAsync_SessionFailure_RecreatesWithNextProxy()
    {
        var handler = new QueueHandler([
            JsonOk("""{"status":"ok","message":"Session created"}"""),
            JsonOk("""{"status":"error","message":"Unable to find session"}"""),
            JsonOk("""{"status":"ok","message":"Session created"}"""),
            JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>retry</html>","cookies":[]}}""")
        ]);

        var config = EnabledConfig();
        config.Proxy.List.Add("http://p1.example:8080");
        config.Proxy.List.Add("http://p2.example:8080");

        using var client = CreateClient(handler, config);
        var solution = await client.GetAsync(
            "https://rutracker.org/",
            cookieHeader: null,
            proxy: null,
            CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal("<html>retry</html>", solution.Html);
        Assert.Equal(4, handler.Bodies.Count);

        using var firstCreate = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("sessions.create", firstCreate.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("http://p1.example:8080", firstCreate.RootElement.GetProperty("proxy").GetProperty("url").GetString());

        using var failedGet = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("request.get", failedGet.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("http://p1.example:8080", failedGet.RootElement.GetProperty("proxy").GetProperty("url").GetString());

        using var secondCreate = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal("sessions.create", secondCreate.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("http://p2.example:8080", secondCreate.RootElement.GetProperty("proxy").GetProperty("url").GetString());

        using var retryGet = JsonDocument.Parse(handler.Bodies[3]);
        Assert.Equal("http://p2.example:8080", retryGet.RootElement.GetProperty("proxy").GetProperty("url").GetString());
    }

    [Fact]
    public async Task EnsureSessionAsync_StartsBrowserBeforeFirstFetch()
    {
        var handler = new QueueHandler([
            JsonOk("""{"status":"ok","message":"","sessions":[]}"""),
            JsonOk("""{"status":"ok","message":"Session created"}"""),
            JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>ok</html>","cookies":[]}}""")
        ]);

        using var client = CreateClient(handler);
        Assert.True(await client.EnsureSessionAsync(CancellationToken.None));

        var solution = await client.GetAsync(
            "https://rutracker.org/",
            cookieHeader: null,
            proxy: null,
            CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(3, handler.Bodies.Count);
        using var ping = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("sessions.list", ping.RootElement.GetProperty("cmd").GetString());
        using var create = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("sessions.create", create.RootElement.GetProperty("cmd").GetString());
        using var get = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal("request.get", get.RootElement.GetProperty("cmd").GetString());
    }

    [Fact]
    public async Task GetAsync_DifferentHosts_RunInParallel()
    {
        var handler = new ParallelHostHandler();

        using var client = CreateClient(handler);
        var rutracker = client.GetAsync("https://rutracker.org/", null, null, CancellationToken.None);
        var kinozal = client.GetAsync("https://kinozal.tv/", null, null, CancellationToken.None);

        var results = await Task.WhenAll(rutracker, kinozal);

        Assert.Equal("<html>rt</html>", results[0]?.Html);
        Assert.Equal("<html>kz</html>", results[1]?.Html);

        var creates = new List<string?>();
        foreach (var body in handler.Bodies)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.GetProperty("cmd").GetString() != "sessions.create")
                continue;
            creates.Add(doc.RootElement.GetProperty("session").GetString());
        }

        Assert.Contains("potok_rutracker_org", creates);
        Assert.Contains("potok_kinozal_tv", creates);
    }

    [Fact]
    public async Task EnsureSessionAsync_CallerCanceled_Throws()
    {
        var handler = new HangHandler();
        using var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var task = client.EnsureSessionAsync(cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task GetAsync_Disabled_ReturnsNullWithoutHttp()
    {
        var handler = new QueueHandler([]);
        using var client = CreateClient(handler, enabled: false);

        var solution = await client.GetAsync("https://rutracker.org/", null, null, CancellationToken.None);

        Assert.Null(solution);
        Assert.Empty(handler.Bodies);
    }

    private static FlareSolverrClient CreateClient(HttpMessageHandler handler, bool enabled = true)
    {
        return CreateClient(handler, EnabledConfig(enabled));
    }

    private static FlareSolverrClient CreateClient(HttpMessageHandler handler, Config config)
    {
        var monitor = new StaticOptionsMonitor<Config>(config);
        return new FlareSolverrClient(
            new SingleClientFactory(new HttpClient(handler)),
            monitor,
            new TrackerProxyPool(monitor),
            NullLogger<FlareSolverrClient>.Instance);
    }

    private static Config EnabledConfig(bool enabled = true)
    {
        return new Config
        {
            FlareSolverr = new FlareSolverrSettings
            {
                Enable = enabled,
                Url = "http://127.0.0.1:8191/v1",
                MaxTimeoutMs = 1000,
                SessionIdleMinutes = 0
            }
        };
    }

    private static HttpResponseMessage JsonOk(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ParallelHostHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _bothCreates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _creates;
        private readonly object _lock = new();

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;

            lock (_lock)
                Bodies.Add(body);

            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            var cmd = doc.RootElement.TryGetProperty("cmd", out var cmdEl) ? cmdEl.GetString() : null;
            var url = doc.RootElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";

            if (cmd == "sessions.create")
            {
                var n = Interlocked.Increment(ref _creates);
                if (n == 2)
                    _bothCreates.TrySetResult();
                await _bothCreates.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return JsonOk("""{"status":"ok","message":"Session created"}""");
            }

            if (url.Contains("rutracker", StringComparison.OrdinalIgnoreCase))
                return JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>rt</html>","cookies":[]}}""");
            if (url.Contains("kinozal", StringComparison.OrdinalIgnoreCase))
                return JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>kz</html>","cookies":[]}}""");

            return JsonOk("""{"status":"ok","solution":{"status":200,"response":"<html>?</html>","cookies":[]}}""");
        }
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("hang");
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            else
                Bodies.Add(string.Empty);

            return _responses.Dequeue();
        }
    }
}
