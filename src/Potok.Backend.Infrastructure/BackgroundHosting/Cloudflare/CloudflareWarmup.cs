using Potok.Backend.Core.Enums;

namespace Potok.Backend.Infrastructure.BackgroundHosting.Cloudflare;

internal static class CloudflareWarmup
{
    public static IReadOnlyList<string> ProbeUrls(IEnumerable<ITrackerSearch> trackers, Config config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();

        foreach (var tracker in trackers)
        {
            if (!tracker.Tracker.IsSearchEnabled(config))
                continue;

            if (!Uri.TryCreate(tracker.Host, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                continue;

            if (!seen.Add(uri.Host))
                continue;

            urls.Add(uri.GetLeftPart(UriPartial.Authority) + "/");
        }

        return urls;
    }
}
