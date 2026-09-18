using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.CompositionTests;

public class TrackerProxyPoolTests
{
    [Fact]
    public void TryParse_Socks5WithUserInfo_StripsCredentialsFromUrl()
    {
        var parsed = ProxyEndpoint.TryParse("socks5://qVmtrJ:vvyWFd@46.161.45.253:9740");
        Assert.NotNull(parsed);
        Assert.Equal("socks5://46.161.45.253:9740", parsed.Url);
        Assert.Equal("qVmtrJ", parsed.Username);
        Assert.Equal("vvyWFd", parsed.Password);
        Assert.Equal(new Uri("socks5://46.161.45.253:9740"), parsed.ProxyUri);
    }

    [Fact]
    public void TryParse_HttpWithoutAuth_KeepsUrl()
    {
        var parsed = ProxyEndpoint.TryParse("http://proxy.example.com:8080");
        Assert.NotNull(parsed);
        Assert.Equal("http://proxy.example.com:8080", parsed.Url);
        Assert.Null(parsed.Username);
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void TryParse_BlankOrUnsupported_ReturnsNull()
    {
        Assert.Null(ProxyEndpoint.TryParse(" "));
        Assert.Null(ProxyEndpoint.TryParse("ftp://proxy.example.com:21"));
        Assert.Null(ProxyEndpoint.TryParse("not-a-url"));
    }

    [Fact]
    public void Next_EmptyList_ReturnsNull()
    {
        var pool = CreatePool();
        Assert.False(pool.HasProxies);
        Assert.Null(pool.Next());
        Assert.Null(pool.StickyFlareProxy());
    }

    [Fact]
    public void Next_RoundRobinsConfiguredUrls()
    {
        var pool = CreatePool(
            "http://p1.example:8080",
            "http://p2.example:8080",
            " ");

        Assert.Equal("http://p1.example:8080", pool.Next()?.Url);
        Assert.Equal("http://p2.example:8080", pool.Next()?.Url);
        Assert.Equal("http://p1.example:8080", pool.Next()?.Url);
    }

    [Fact]
    public void StickyFlareProxy_PinsUntilCleared()
    {
        var pool = CreatePool(
            "http://u:s@p1.example:8080",
            "http://p2.example:8080");

        var first = pool.StickyFlareProxy();
        var again = pool.StickyFlareProxy();
        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Equal("http://p1.example:8080", first.Url);
        Assert.Equal("u", first.Username);
        Assert.Equal("s", first.Password);

        pool.ClearSticky();
        var next = pool.StickyFlareProxy();
        Assert.NotNull(next);
        Assert.Equal("http://p2.example:8080", next.Url);
    }

    [Fact]
    public void RotatingWebProxy_GetProxy_CyclesAndResolvesCredentials()
    {
        var pool = CreatePool(
            "socks5://u:s@p1.example:1080",
            "http://p2.example:8080");
        var proxy = new RotatingWebProxy(pool);
        var dest = new Uri("https://rutracker.org/");

        Assert.Equal(new Uri("socks5://p1.example:1080"), proxy.GetProxy(dest));
        Assert.Equal(new Uri("http://p2.example:8080"), proxy.GetProxy(dest));
        Assert.False(proxy.IsBypassed(dest));

        var cred = proxy.Credentials?.GetCredential(new Uri("socks5://p1.example:1080"), "Basic");
        Assert.Equal("u", cred?.UserName);
        Assert.Equal("s", cred?.Password);
        Assert.Null(proxy.Credentials?.GetCredential(new Uri("http://p2.example:8080"), "Basic"));
    }

    [Fact]
    public void RotatingWebProxy_BypassesLoopbackWhenConfigured()
    {
        var pool = CreatePool(bypassOnLocal: true, "http://p1.example:8080");
        var proxy = new RotatingWebProxy(pool);

        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1/")));
        Assert.False(proxy.IsBypassed(new Uri("https://rutracker.org/")));
    }

    private static TrackerProxyPool CreatePool(params string[] items)
    {
        return CreatePool(bypassOnLocal: false, items);
    }

    private static TrackerProxyPool CreatePool(bool bypassOnLocal, params string[] items)
    {
        var config = new Config
        {
            Proxy = new ProxySettings
            {
                BypassOnLocal = bypassOnLocal,
                List = [.. items]
            }
        };
        return new TrackerProxyPool(new StaticOptionsMonitor<Config>(config));
    }
}
