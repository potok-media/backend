using MonoTorrent;

namespace Potok.Backend.Core.Extensions;

public static class MagnetLinkExtensions
{
    public static string? AnnounceName(this string magnet)
    {
        try
        {
            return MagnetLink.Parse(magnet).Name;
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<string>? AnnounceUrls(this string magnet)
    {
        try
        {
            return MagnetLink.Parse(magnet).AnnounceUrls ?? Enumerable.Empty<string>();
        }
        catch
        {
            return null;
        }
    }
}