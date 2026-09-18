using System.Net;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.Infrastructure.Http;

/// <summary>
/// Shared proxy pool for tracker egress. Direct HttpClient calls rotate per request so one IP
/// does not eat the whole search. FlareSolverr keeps one sticky proxy for the life of a browser
/// session — Cloudflare clearance is bound to that IP.
/// </summary>
public sealed class TrackerProxyPool
{
    private readonly IOptionsMonitor<Config> _config;
    private readonly object _stickyLock = new();
    private int _cursor = -1;
    private FlareSolverrProxy? _sticky;

    public TrackerProxyPool(IOptionsMonitor<Config> config)
    {
        _config = config;
    }

    public bool BypassOnLocal => _config.CurrentValue.Proxy.BypassOnLocal;

    public IReadOnlyList<ProxyEndpoint> Items =>
        _config.CurrentValue.Proxy.List
            .Select(ProxyEndpoint.TryParse)
            .OfType<ProxyEndpoint>()
            .ToArray();

    public bool HasProxies => Items.Count > 0;

    public ProxyEndpoint? Next()
    {
        var items = Items;
        if (items.Count == 0)
            return null;
        var index = Interlocked.Increment(ref _cursor);
        return items[(int)(unchecked((uint)index) % (uint)items.Count)];
    }

    public FlareSolverrProxy? StickyFlareProxy()
    {
        lock (_stickyLock)
        {
            if (_sticky is not null)
                return _sticky;
            var item = Next();
            if (item is null)
                return null;
            _sticky = new FlareSolverrProxy(item.Url, item.Username, item.Password);
            return _sticky;
        }
    }

    public void ClearSticky()
    {
        lock (_stickyLock)
            _sticky = null;
    }

    public NetworkCredential? CredentialFor(Uri proxyUri)
    {
        var match = Items.FirstOrDefault(i => i.ProxyUri == proxyUri);
        if (match is null || string.IsNullOrEmpty(match.Username))
            return null;
        return new NetworkCredential(match.Username, match.Password);
    }
}

public sealed class RotatingWebProxy : IWebProxy
{
    private readonly TrackerProxyPool _pool;

    public RotatingWebProxy(TrackerProxyPool pool)
    {
        _pool = pool;
        Credentials = new PoolCredentials(pool);
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
    {
        return _pool.Next()?.ProxyUri;
    }

    public bool IsBypassed(Uri host)
    {
        if (!_pool.HasProxies)
            return true;
        if (_pool.BypassOnLocal && host.IsLoopback)
            return true;
        return false;
    }

    private sealed class PoolCredentials : ICredentials
    {
        private readonly TrackerProxyPool _pool;
        public PoolCredentials(TrackerProxyPool pool) => _pool = pool;

        public NetworkCredential? GetCredential(Uri uri, string authType) => _pool.CredentialFor(uri);
    }
}
