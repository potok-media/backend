using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TmdbClient
{
    private readonly HttpClient _httpClient;
    private readonly GatewayOptions _options;

    public TmdbClient(HttpClient httpClient, IOptions<GatewayOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<T?> GetAsync<T>(string path, string language = "ru")
    {
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{path}{separator}api_key={_options.TmdbApiKey}&language={language}";
        
        return await _httpClient.GetFromJsonAsync<T>(url);
    }

    public async Task<JsonElement> GetAsync(string path, string language = "ru")
    {
        return await GetAsync<JsonElement>(path, language);
    }
}
