using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class SearchEngineClient : ISearchEngineClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ITorrentRepository _torrentRepository;
    private readonly GatewayOptions _options;

    public SearchEngineClient(
        HttpClient httpClient,
        ISettingsRepository settingsRepository,
        ITorrentRepository torrentRepository,
        IOptions<GatewayOptions> options)
    {
        _httpClient = httpClient;
        _settingsRepository = settingsRepository;
        _torrentRepository = torrentRepository;
        _options = options.Value;
    }

    public async Task<TorrentSearchResponse> SearchAsync(TorrentSearchRequest request)
    {
        var searchEngineUrl = await _settingsRepository.GetValueAsync("searchEngineUrl") ?? _options.DefaultSearchEngineUrl;
        var url = $"{searchEngineUrl.TrimEnd('/')}/api/v1/torrents/search";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, request);
            if (!response.IsSuccessStatusCode) return new TorrentSearchResponse(new List<TorrentSearchResult>());

            var result = await response.Content.ReadFromJsonAsync<TorrentSearchResponse>();
            if (result?.Results == null) return new TorrentSearchResponse(new List<TorrentSearchResult>());

            // Enrich with overrides
            var enrichedResults = new List<TorrentSearchResult>();
            foreach (var torrent in result.Results)
            {
                var hash = GetHashFromMagnet(torrent.MagnetUri);
                TorrentOverride? @override = null;
                
                if (!string.IsNullOrEmpty(hash))
                {
                    var dbOverride = await _torrentRepository.GetOverrideAsync(hash);
                    if (dbOverride != null)
                    {
                        @override = new TorrentOverride(dbOverride.Hash, dbOverride.Season, dbOverride.EpisodeOffset);
                    }
                }
                
                enrichedResults.Add(torrent with { Override = @override });
            }

            return new TorrentSearchResponse(enrichedResults);
        }
        catch
        {
            return new TorrentSearchResponse(new List<TorrentSearchResult>());
        }
    }

    private string? GetHashFromMagnet(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet)) return null;
        try
        {
            var match = Regex.Match(magnet, @"xt=urn:btih:([^&/]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLower() : null;
        }
        catch { return null; }
    }
}
