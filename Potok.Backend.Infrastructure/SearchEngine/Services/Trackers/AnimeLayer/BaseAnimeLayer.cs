using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers.AnimeLayer;

public abstract class BaseAnimeLayer : BaseTrackerSearch
{
    protected const string CookieKey = "animelayer:cookies";
    protected static readonly Encoding Encoding = Encoding.GetEncoding("windows-1251");

    protected BaseAnimeLayer(ICacheService cacheService, HttpService httpService, IOptionsSnapshot<Config> config)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.AnimeLayer;
    public override string TrackerName => "animelayer";
    public override string Host => "http://animelayer.ru";

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent)
    {
        var html = await Get(torrent.Url, torrent.Url);
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var magnet = await GetMagnet(torrent.Url);
        if (!string.IsNullOrWhiteSpace(magnet))
        {
            torrent.Magnet = magnet;
            return true;
        }

        return false;
    }

    protected async Task<string> Get(string url, string? referer = null)
    {
        var cookie = await Authorize();
        var html = await HttpService.GetStringAsync(url, new RequestOptions { Encoding = Encoding, Cookie = cookie, Referer = referer });

        if (html.Contains("action=login"))
        {
            cookie = await Authorize(true);
            html = await HttpService.GetStringAsync(url, new RequestOptions { Encoding = Encoding, Cookie = cookie, Referer = referer });
        }

        return html;
    }

    private async Task<string?> GetMagnet(string url)
    {
        var cookie = await Authorize();
        var html = await HttpService.GetStringAsync(url, new RequestOptions { Encoding = Encoding, Cookie = cookie });
        var match = Regex.Match(html, "href=\"(magnet:[^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<string> Authorize(bool reAuth = false)
    {
        if (!reAuth)
        {
            if (CacheService.TryGetValue(CookieKey, out string? cachedCookie))
                return cachedCookie!;
        }

        var login = Config.AnimeLayer.Authorization.Login;
        var password = Config.AnimeLayer.Authorization.Password;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return string.Empty;

        var url = $"{Host}/auth/login/";
        var response = await HttpService.PostResponseAsync(url, null, new RequestOptions 
        { 
            Cookie = $"login={login}; password={password}", 
            AllowAutoRedirect = false 
        });

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var cookie = string.Join("; ", cookies);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                await CacheService.SetAsync(CookieKey, cookie, TimeSpan.FromDays(Config.Cache.AuthExpiry));
                return cookie;
            }
        }

        return string.Empty;
    }
}