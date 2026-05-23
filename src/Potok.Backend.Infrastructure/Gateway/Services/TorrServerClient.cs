using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TorrServerClient : ITorrServerClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ITorrentRepository _torrentRepository;
    private readonly GatewayOptions _options;

    public TorrServerClient(
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

    private async Task<TorrServerConfig?> GetTorrServerConfigAsync()
    {
        var json = await _settingsRepository.GetValueAsync("torrServer");
        if (string.IsNullOrEmpty(json))
        {
            if (!string.IsNullOrEmpty(_options.DefaultTorrServerUrl))
            {
                return new TorrServerConfig(_options.DefaultTorrServerUrl, false, null, null);
            }
            return new TorrServerConfig("http://localhost:5050", false, null, null);
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var setting = JsonSerializer.Deserialize<TorrServerSetting>(json, options);
            if (!string.IsNullOrEmpty(setting?.BaseUrl))
            {
                return new TorrServerConfig(setting.BaseUrl, setting.AuthEnabled, setting.AuthLogin, setting.AuthPassword, setting.SaveToDb ?? true);
            }
        }
        catch { /* Ignore */ }

        return new TorrServerConfig("http://localhost:5050", false, null, null);
    }

    public async Task<TorrentFilesResponse> GetFilesAsync(TorrentFilesRequest request)
    {
        var config = await GetTorrServerConfigAsync();
        if (config == null) throw new Exception("TORRSERVER_NOT_CONFIGURED");

        var link = request.MagnetUri ?? request.Link;
        if (string.IsNullOrEmpty(link)) throw new Exception("TORRSERVER_LINK_EMPTY");

        ConfigureHttpClient(config);

        var response = await _httpClient.PostAsJsonAsync("/api/torrent/files", request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"TORRENT_SERVICE_ERROR: {response.StatusCode} - {errorContent}");
        }

        var result = await response.Content.ReadFromJsonAsync<TorrentFilesResponse>();
        if (result == null || string.IsNullOrEmpty(result.Hash)) throw new Exception("TORRENT_SERVICE_RESPONSE_EMPTY");

        var hash = result.Hash.ToLower();

        // Fetch override if exists
        var torrentOverride = await _torrentRepository.GetOverrideAsync(hash);

        if (request.MediaType == "tv" && torrentOverride != null && result.Items != null)
        {
            var items = result.Items.ToList();
            for (int j = 0; j < items.Count; j++)
            {
                int? seasonNum = items[j].Season;
                int? episodeNum = items[j].Episode;

                if (torrentOverride.Season.HasValue) seasonNum = torrentOverride.Season;
                if (torrentOverride.EpisodeOffset.HasValue)
                {
                    episodeNum = (j + 1) + torrentOverride.EpisodeOffset.Value;
                }

                items[j] = items[j] with { Season = seasonNum, Episode = episodeNum };
            }
            return result with { Items = items };
        }

        return result;
    }

    public async Task<TorrentStreamResponse> GetStreamUrlAsync(TorrentStreamRequest request)
    {
        var config = await GetTorrServerConfigAsync();
        if (config == null) throw new Exception("TORRSERVER_NOT_CONFIGURED");

        var hash = GetHashFromMagnet(request.MagnetUri) ?? request.Hash;
        var baseUrl = config.BaseUrl.TrimEnd('/');
        
        var streamUrl = GenerateStreamUrl(baseUrl, hash, request.Index, request.MediaType, request.Season, request.Episode, request.EnglishTitle, request.OriginalTitle, request.Title, request.TmdbId?.ToString());

        return new TorrentStreamResponse(streamUrl);
    }

    public async Task<IEnumerable<string>> GetNormalizedStreamUrlsAsync(TorrentFilesRequest request)
    {
        var config = await GetTorrServerConfigAsync();
        if (config == null) throw new Exception("TORRSERVER_NOT_CONFIGURED");

        var filesResponse = await GetFilesAsync(request);
        if (filesResponse.Items == null || !filesResponse.Items.Any()) return Enumerable.Empty<string>();

        var baseUrl = config.BaseUrl.TrimEnd('/');
        var hash = filesResponse.Hash ?? "";
        
        return filesResponse.Items.Select(file => 
            GenerateStreamUrl(baseUrl, hash, file.Id, request.MediaType, file.Season, file.Episode, request.EnglishTitle, request.OriginalTitle, request.Title, request.TmdbId?.ToString())
        ).ToList();
    }

    private string GenerateStreamUrl(string baseUrl, string hash, string index, string? mediaType, int? season, int? episode, string? englishTitle, string? originalTitle, string? title, string? tmdbId)
    {
        var rawTitle = englishTitle ?? originalTitle ?? title ?? "{tmdb-" + tmdbId + "}";
        var tmdbTag = "{tmdb-" + tmdbId + "}";
        var cleanTitle = Regex.Replace(rawTitle, @"[^a-zA-Z0-9\s]", "");
        cleanTitle = Regex.Replace(cleanTitle, @"\s+", ".");
        cleanTitle = cleanTitle.Trim('.');

        var fileName = cleanTitle;
        if (mediaType == "tv")
        {
            fileName += $".S{(season ?? 1):D2}E{(episode ?? 1):D2}";
        }
        fileName += $".{tmdbTag}.mkv";
        fileName = fileName.Trim('.');

        return $"{baseUrl}/stream/{hash.ToLower()}/{index}/{fileName}";
    }

    private void ConfigureHttpClient(TorrServerConfig config)
    {
        _httpClient.BaseAddress = new Uri(config.BaseUrl);
        
        if (config.AuthEnabled && !string.IsNullOrEmpty(config.AuthLogin))
        {
            var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.AuthLogin}:{config.AuthPassword}"));
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
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

    private record TorrServerSetting(string BaseUrl, bool AuthEnabled, string? AuthLogin, string? AuthPassword, bool? SaveToDb = true);
    private record TorrServerConfig(string BaseUrl, bool AuthEnabled, string? AuthLogin, string? AuthPassword, bool SaveToDb = true);
}
