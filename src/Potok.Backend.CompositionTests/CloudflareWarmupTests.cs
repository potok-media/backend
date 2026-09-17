using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.BackgroundHosting.Cloudflare;

namespace Potok.Backend.CompositionTests;

public class CloudflareWarmupTests
{
    [Fact]
    public void ProbeUrls_TakesEnabledTrackerOriginsOnly()
    {
        var config = new Config
        {
            RuTracker = { EnableSearch = true },
            NNMClub = { EnableSearch = true },
            RuTor = { EnableSearch = false },
            Kinozal = { EnableSearch = false },
            AnimeLayer = { EnableSearch = false },
            Aniliberty = { EnableSearch = false }
        };

        var urls = CloudflareWarmup.ProbeUrls(
            [
                new FakeTracker(TrackerType.Rutracker, "https://rutracker.org/"),
                new FakeTracker(TrackerType.Rutracker, "https://rutracker.org/forum/index.php"),
                new FakeTracker(TrackerType.NNMClub, "https://nnmclub.to"),
                new FakeTracker(TrackerType.Rutor, "http://rutor.info/")
            ],
            config);

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://rutracker.org/", urls);
        Assert.Contains("https://nnmclub.to/", urls);
        Assert.DoesNotContain(urls, u => u.Contains("rutor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProbeUrls_SkipsInvalidHosts()
    {
        var config = new Config { RuTracker = { EnableSearch = true } };

        var urls = CloudflareWarmup.ProbeUrls(
            [new FakeTracker(TrackerType.Rutracker, "not-a-url")],
            config);

        Assert.Empty(urls);
    }

    private sealed class FakeTracker : ITrackerSearch
    {
        public FakeTracker(TrackerType tracker, string host)
        {
            Tracker = tracker;
            Host = host;
        }

        public TrackerType Tracker { get; }
        public string TrackerName => Tracker.ToString();
        public string Host { get; }

        public Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyCollection<TorrentDetails>>([]);
        }
    }
}
