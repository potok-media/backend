using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Potok.Backend.Core.Models.SearchEngine.Options;
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
        Assert.Equal("potok", create.RootElement.GetProperty("session").GetString());

        using var get = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("request.get", get.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("https://rutracker.org/forum/index.php", get.RootElement.GetProperty("url").GetString());
        Assert.Equal("bb_session", get.RootElement.GetProperty("cookies")[0].GetProperty("name").GetString());
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

    private static FlareSolverrClient CreateClient(QueueHandler handler, bool enabled = true)
    {
        var config = new Config
        {
            FlareSolverr = new FlareSolverrSettings
            {
                Enable = enabled,
                Url = "http://127.0.0.1:8191/v1",
                MaxTimeoutMs = 1000,
                SessionIdleMinutes = 0
            }
        };

        return new FlareSolverrClient(
            new SingleClientFactory(new HttpClient(handler)),
            new StaticOptionsMonitor<Config>(config),
            NullLogger<FlareSolverrClient>.Instance);
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
