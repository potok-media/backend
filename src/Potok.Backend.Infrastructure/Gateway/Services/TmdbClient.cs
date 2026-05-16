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

    public async Task<T?> GetAsync<T>(string path, string language = "ru", int page = 1)
    {
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{path}{separator}api_key={_options.TmdbApiKey}&language={language}";
        
        if (page > 1)
        {
            url += $"&page={page}";
        }
        
        return await _httpClient.GetFromJsonAsync<T>(url);
    }

    public async Task<JsonElement> GetAsync(string path, string language = "ru", int page = 1)
    {
        return await GetAsync<JsonElement>(path, language, page);
    }
}
