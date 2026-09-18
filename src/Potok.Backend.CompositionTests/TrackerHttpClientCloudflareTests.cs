using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.CompositionTests;

public class TrackerHttpClientCloudflareTests
{
    [Fact]
    public async Task GetStringAsync_Ok_DoesNotCallFlareSolverr()
    {
        var flare = new FakeFlareSolverrClient();
        var handler = new ScriptedHandler(_ => OkHtml("<html>ok</html>"));
        var client = CreateClient(handler, flare, enabled: true);

        var html = await client.GetStringAsync("https://rutor.info/search");

        Assert.Equal("<html>ok</html>", html);
        Assert.Empty(flare.Gets);
    }

    [Fact]
    public async Task GetStringAsync_Challenge_UsesFlareSolverrAndMarksGuarded()
    {
        var flare = new FakeFlareSolverrClient
        {
            GetResult = new FlareSolverrSolution(200, "<html>solved</html>", [])
        };
        var handler = new ScriptedHandler(_ => Challenge());
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);
        var client = CreateClient(handler, flare, EnabledSettings(), guard);

        var html = await client.GetStringAsync("https://rutracker.org/forum/tracker.php");

        Assert.Equal("<html>solved</html>", html);
        Assert.Single(flare.Gets);
        Assert.True(guard.IsGuarded("rutracker.org"));
    }

    [Fact]
    public async Task GetStringAsync_GuardedHost_SkipsDirectGet()
    {
        var flare = new FakeFlareSolverrClient
        {
            GetResult = new FlareSolverrSolution(200, "<html>browser</html>", [])
        };
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("direct GET must not run"));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);
        guard.MarkGuarded("rutracker.org");
        var client = CreateClient(handler, flare, EnabledSettings(), guard);

        var html = await client.GetStringAsync("https://rutracker.org/forum/viewtopic.php?t=1");

        Assert.Equal("<html>browser</html>", html);
        Assert.Single(flare.Gets);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetStringAsync_Disabled_IgnoresChallenge()
    {
        var flare = new FakeFlareSolverrClient
        {
            GetResult = new FlareSolverrSolution(200, "<html>solved</html>", [])
        };
        var handler = new ScriptedHandler(_ => Challenge());
        var client = CreateClient(handler, flare, enabled: false);

        var html = await client.GetStringAsync("https://rutracker.org/forum/tracker.php");

        Assert.Equal(string.Empty, html);
        Assert.Empty(flare.Gets);
    }

    [Fact]
    public async Task GetStringAsync_BrowserHtml_IgnoresWindows1251()
    {
        var flare = new FakeFlareSolverrClient
        {
            GetResult = new FlareSolverrSolution(200, "Привет", [])
        };
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);
        guard.MarkGuarded("nnmclub.to");
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("direct GET must not run"));
        var client = CreateClient(handler, flare, EnabledSettings(), guard);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = await client.GetStringAsync(
            "https://nnmclub.to/forum/tracker.php",
            encoding: Encoding.GetEncoding("windows-1251"));

        Assert.Equal("Привет", html);
    }

    [Fact]
    public async Task PostResponseAsync_SynthesizesSetCookie()
    {
        var flare = new FakeFlareSolverrClient
        {
            PostResult = new FlareSolverrSolution(
                200,
                "<html>logged in</html>",
                [new FlareSolverrCookie("bb_session", "abc")])
        };
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(EnabledSettings(), () => now);
        guard.MarkGuarded("rutracker.org");
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("direct POST must not run"));
        var client = CreateClient(handler, flare, EnabledSettings(), guard);

        using var response = await client.PostResponseAsync(
            "https://rutracker.org/forum/login.php",
            new StringContent("login=1"),
            allowRedirect: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains("bb_session=abc", cookies);
        Assert.Single(flare.Posts);
        Assert.Equal("login=1", flare.Posts[0].PostData);
    }

    private static TrackerHttpClient CreateClient(ScriptedHandler handler, FakeFlareSolverrClient flare, bool enabled)
    {
        var settings = enabled ? EnabledSettings() : new FlareSolverrSettings();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return CreateClient(handler, flare, settings, CreateGuard(settings, () => now));
    }

    private static TrackerHttpClient CreateClient(
        ScriptedHandler handler,
        FakeFlareSolverrClient flare,
        FlareSolverrSettings settings,
        CloudflareGuard guard)
    {
        var config = new Config { FlareSolverr = settings };
        var monitor = new StaticOptionsMonitor<Config>(config);
        return new TrackerHttpClient(
            new StubHttpClientFactory(handler),
            monitor,
            guard,
            flare,
            new TrackerProxyPool(monitor),
            NullLogger<TrackerHttpClient>.Instance);
    }

    [Fact]
    public async Task GetStringAsync_ProxyTransportError_RetriesNextProxy()
    {
        var flare = new FakeFlareSolverrClient();
        var calls = 0;
        var handler = new ScriptedHandler(_ =>
        {
            calls++;
            if (calls == 1)
                throw new HttpRequestException("proxy dead");
            return OkHtml("<html>ok</html>");
        });

        var settings = EnabledSettings();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(settings, () => now);
        var config = new Config
        {
            FlareSolverr = settings,
            Proxy = new ProxySettings
            {
                List = ["http://p1.example:8080", "http://p2.example:8080"]
            }
        };
        var monitor = new StaticOptionsMonitor<Config>(config);
        var client = new TrackerHttpClient(
            new StubHttpClientFactory(handler),
            monitor,
            guard,
            flare,
            new TrackerProxyPool(monitor),
            NullLogger<TrackerHttpClient>.Instance);

        var html = await client.GetStringAsync("https://rutor.info/search");

        Assert.Equal("<html>ok</html>", html);
        Assert.Equal(2, calls);
        Assert.Empty(flare.Gets);
    }

    [Fact]
    public async Task GetStringAsync_AllProxiesFail_ReturnsEmptyWithoutThrowing()
    {
        var flare = new FakeFlareSolverrClient();
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("eof"));
        var settings = EnabledSettings();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = CreateGuard(settings, () => now);
        var config = new Config
        {
            FlareSolverr = settings,
            Proxy = new ProxySettings { List = ["http://p1.example:8080", "http://p2.example:8080"] }
        };
        var monitor = new StaticOptionsMonitor<Config>(config);
        var client = new TrackerHttpClient(
            new StubHttpClientFactory(handler),
            monitor,
            guard,
            flare,
            new TrackerProxyPool(monitor),
            NullLogger<TrackerHttpClient>.Instance);

        var html = await client.GetStringAsync("https://kinozal.tv/browse.php");

        Assert.Equal(string.Empty, html);
    }

    private static CloudflareGuard CreateGuard(FlareSolverrSettings settings, Func<DateTime> utcNow)
    {
        return new CloudflareGuard(
            new StaticOptionsMonitor<Config>(new Config { FlareSolverr = settings }),
            NullLogger<CloudflareGuard>.Instance,
            utcNow);
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

    private static HttpResponseMessage OkHtml(string html)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
    }

    private static HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Just a moment", Encoding.UTF8, "text/html")
        };
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
        return response;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler);
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _onSend;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> onSend)
        {
            _onSend = onSend;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_onSend(request));
        }
    }

    private sealed class FakeFlareSolverrClient : IFlareSolverrClient
    {
        public FlareSolverrSolution? GetResult { get; set; }
        public FlareSolverrSolution? PostResult { get; set; }
        public List<string> Gets { get; } = [];
        public List<(string Url, string? PostData)> Posts { get; } = [];

        public Task<FlareSolverrSolution?> GetAsync(
            string url,
            string? cookieHeader,
            FlareSolverrProxy? proxy,
            CancellationToken ct)
        {
            Gets.Add(url);
            return Task.FromResult(GetResult);
        }

        public Task<FlareSolverrSolution?> PostAsync(
            string url,
            string? postData,
            string? cookieHeader,
            FlareSolverrProxy? proxy,
            CancellationToken ct)
        {
            Posts.Add((url, postData));
            return Task.FromResult(PostResult);
        }

        public Task<bool> WarmupAsync(string url, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> EnsureSessionAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
