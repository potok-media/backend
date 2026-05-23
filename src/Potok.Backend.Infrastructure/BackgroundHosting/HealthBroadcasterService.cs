using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;
using System.Text.Json;

namespace Potok.Backend.Infrastructure.BackgroundHosting;

public class HealthBroadcasterService : BackgroundService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly GatewayOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEventBroadcaster _broadcaster;

    private string _lastSearchStatus = "unknown";
    private string _lastTorrentStatus = "unknown";

    public HealthBroadcasterService(
        ISettingsRepository settingsRepository,
        IOptions<GatewayOptions> options,
        IHttpClientFactory httpClientFactory,
        IEventBroadcaster broadcaster)
    {
        _settingsRepository = settingsRepository;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _broadcaster = broadcaster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Periodic check every 5 seconds
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var searchUrl = await _settingsRepository.GetValueAsync("searchEngineUrl");
                var searchStatus = await CheckHealthAsync(searchUrl);

                var torrentUrl = await GetTorrServerUrlAsync();
                var torrentStatus = await CheckHealthAsync(torrentUrl);

                if (searchStatus != _lastSearchStatus || torrentStatus != _lastTorrentStatus)
                {
                    _lastSearchStatus = searchStatus;
                    _lastTorrentStatus = torrentStatus;

                    _broadcaster.Publish("health-changed", new
                    {
                        searchEngine = new { configured = searchStatus != "unconfigured", online = searchStatus == "online" },
                        torrServer = new { configured = torrentStatus != "unconfigured", online = torrentStatus == "online" }
                    });
                }
            }
            catch
            {
                // Catch all to prevent background thread crash
            }
        }
    }

    private async Task<string> CheckHealthAsync(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return "unconfigured";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/health");
            return response.IsSuccessStatusCode ? "online" : "offline";
        }
        catch
        {
            return "offline";
        }
    }

    private async Task<string?> GetTorrServerUrlAsync()
    {
        var json = await _settingsRepository.GetValueAsync("torrServer");
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("baseUrl", out var prop))
            {
                return prop.GetString();
            }
        }
        catch { }

        return null;
    }
}
