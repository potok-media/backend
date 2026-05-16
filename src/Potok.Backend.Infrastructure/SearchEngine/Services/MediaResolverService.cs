using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services;

public class MediaResolverService : IMediaResolverService
{
    private readonly ICacheService _cacheService;
    private readonly Config _config;
    private readonly TrackerHttpClient _httpService;

    public MediaResolverService(
        ICacheService cacheService,
        TrackerHttpClient httpService,
        IOptions<Config> config)
    {
        _cacheService = cacheService;
        _httpService = httpService;
        _config = config.Value;
    }

    public async Task<(string? search, string? altname)> ResolveKpImdb(string? search, string? altname)
    {
        if (string.IsNullOrWhiteSpace(search))
            return (search, altname);

        var trimmedSearch = search.Trim();
        if (!Regex.IsMatch(trimmedSearch, "^((tt|kp)[0-9]+|[0-9]+)$"))
            return (search, altname);

        var cacheKey = CacheKeyBuilder.Build("api", "v1.0", "torrents", trimmedSearch);
        var cache = await _cacheService.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                string uri;
                if (trimmedSearch.StartsWith("kp"))
                    uri = $"&kp={trimmedSearch[2..]}";
                else if (trimmedSearch.StartsWith("tt"))
                    uri = $"&imdb={trimmedSearch}";
                else
                    uri = $"&tmdb={trimmedSearch}";

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var json = await _httpService.GetStringAsync(
                        $"https://api.alloha.tv/?token=04941a9a3ca3ac16e2b4327347bbc1{uri}", 
                        ct: cts.Token);
                    
                    if (string.IsNullOrWhiteSpace(json)) return (null, null);
                    
                    var response = JsonConvert.DeserializeObject<JObject>(json);
                    var data = response?.Value<JObject>("data");
                    return (data?.Value<string>("original_name"), data?.Value<string>("name"));
                }
                catch
                {
                    return (null, null);
                }
            },
            TimeSpan.FromMinutes(_config.Cache.Expiry));

        return !string.IsNullOrWhiteSpace(cache.Item1) && !string.IsNullOrWhiteSpace(cache.Item2)
            ? (cache.Item1, cache.Item2)
            : (cache.Item1 ?? cache.Item2, altname);
    }
}