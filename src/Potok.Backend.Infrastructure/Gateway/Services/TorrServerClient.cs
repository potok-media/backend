using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Infrastructure.Configuration;
using TorrentTitleParser;

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
            return null;
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

        return null;
    }

    public async Task<TorrentFilesResponse> GetFilesAsync(TorrentFilesRequest request)
    {
        var config = await GetTorrServerConfigAsync();
        if (config == null) throw new Exception("TORRSERVER_NOT_CONFIGURED");

        var link = request.MagnetUri ?? request.Link;
        if (string.IsNullOrEmpty(link)) throw new Exception("TORRSERVER_LINK_EMPTY");

        ConfigureHttpClient(config);

        // 1. Add torrent to TorrServer
        var addPayload = new
        {
            action = "add",
            link = link,
            title = $"[POTOK] {request.Title}",
            poster = request.Poster,
            save_to_db = config.SaveToDb,
            data = "{ \"lampa\": true }"
        };

        var response = await _httpClient.PostAsJsonAsync("/torrents", addPayload);
        if (!response.IsSuccessStatusCode) throw new Exception($"TORRSERVER_{response.StatusCode}");

        var hashPayload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rawHash = hashPayload.TryGetProperty("hash", out var h) ? h.GetString() : hashPayload.TryGetProperty("Hash", out var h2) ? h2.GetString() : null;

        if (string.IsNullOrEmpty(rawHash)) throw new Exception("TORRSERVER_HASH_EMPTY");
        
        var hash = rawHash.ToLower();

        // Fetch override if exists
        var torrentOverride = await _torrentRepository.GetOverrideAsync(hash);

        // 2. Poll for files
        for (int i = 0; i < 15; i++)
        {
            var getPayload = new { action = "get", hash = hash };
            var getResponse = await _httpClient.PostAsJsonAsync("/torrents", getPayload);
            if (getResponse.IsSuccessStatusCode)
            {
                var filesPayload = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
                if (filesPayload.TryGetProperty("file_stats", out var stats) && stats.ValueKind == JsonValueKind.Array)
                {
                    var videoExtensions = new[] { ".mkv", ".mp4", ".avi", ".ts", ".mov" };
                    
                    var allFiles = stats.EnumerateArray().Select((item, index) => {
                        var path = item.TryGetProperty("path", out var p) ? p.GetString() : "";
                        var name = item.TryGetProperty("name", out var t) ? t.GetString() : "";
                        var ext = !string.IsNullOrEmpty(path) ? Path.GetExtension(path) : ".mkv";
                        
                        var parsed = new Torrent(path);
                        if (!parsed.Season.HasValue && !string.IsNullOrEmpty(name)) {
                            parsed = new Torrent(name);
                        }

                        return new {
                            Index = index + 1,
                            Path = path,
                            Name = name,
                            Ext = ext,
                            ParsedSeason = parsed.Season,
                            ParsedEpisode = parsed.Episode,
                            SizeBytes = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : (long?)null
                        };
                    }).ToList();

                    var videoFiles = allFiles
                        .Where(f => videoExtensions.Contains(f.Ext.ToLower()))
                        .OrderBy(f => f.Path)
                        .ToList();

                    var resultItems = new List<TorrentFileItem>();
                    for (int j = 0; j < videoFiles.Count; j++)
                    {
                        var f = videoFiles[j];
                        int? seasonNum = f.ParsedSeason;
                        int? episodeNum = f.ParsedEpisode;

                        if (request.MediaType == "tv" && torrentOverride != null) {
                            if (torrentOverride.Season.HasValue) seasonNum = torrentOverride.Season;
                            if (torrentOverride.EpisodeOffset.HasValue) {
                                episodeNum = (j + 1) + torrentOverride.EpisodeOffset.Value;
                            }
                        }

                        resultItems.Add(new TorrentFileItem(
                            Id: f.Index.ToString(),
                            Title: !string.IsNullOrEmpty(f.Name) ? f.Name : null,
                            SizeLabel: null,
                            SizeBytes: f.SizeBytes,
                            Path: f.Path,
                            Season: seasonNum,
                            Episode: episodeNum,
                            IsSerial: request.MediaType == "tv",
                            FolderName: "",
                            Extension: f.Ext
                        ));
                    }

                    if (resultItems.Any()) {
                        return new TorrentFilesResponse(hash, resultItems);
                    }
                }
            }
            await Task.Delay(1500);
        }

        throw new Exception("TORRSERVER_FILES_TIMEOUT");
    }

    public async Task<TorrentStreamResponse> GetStreamUrlAsync(TorrentStreamRequest request)
    {
        var config = await GetTorrServerConfigAsync();
        if (config == null) throw new Exception("TORRSERVER_NOT_CONFIGURED");

        var hash = GetHashFromMagnet(request.MagnetUri) ?? request.Hash;
        var baseUrl = config.BaseUrl.TrimEnd('/');
        
        var streamUrl = GenerateStreamUrl(baseUrl, hash, request.Index, request.MediaType, request.Season, request.Episode, request.EnglishTitle, request.OriginalTitle, request.Title, request.TmdbId);

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
            GenerateStreamUrl(baseUrl, hash, file.Id, request.MediaType, file.Season, file.Episode, request.EnglishTitle, request.OriginalTitle, request.Title, request.TmdbId)
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

        return $"{baseUrl}/stream/{fileName}?link={hash.ToLower()}&index={index}&play";
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
