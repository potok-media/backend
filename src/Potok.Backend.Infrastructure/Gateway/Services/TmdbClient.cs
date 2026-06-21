using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TmdbClient
{
    private readonly HttpClient _httpClient;
    private readonly GatewayOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TmdbClient(HttpClient httpClient, IOptions<GatewayOptions> options, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    // The language of the current request (?language=), defaulting to English. Lets callers
    // (orchestrators/mappers) localize non-TMDB-fetched bits like logo selection consistently.
    public string CurrentLanguage => ResolveLanguage(null);

    // Resolve the UI language for a TMDB request: an explicit argument wins, otherwise the
    // current request's ?language= query parameter, otherwise English (the source language).
    private string ResolveLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            return language!;

        var fromRequest = _httpContextAccessor.HttpContext?.Request.Query["language"].ToString();
        return string.IsNullOrWhiteSpace(fromRequest) ? "en" : fromRequest!;
    }

    public async Task<T?> GetAsync<T>(string path, string? language = null, int page = 1)
    {
        var lang = ResolveLanguage(language);
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{path}{separator}api_key={_options.TmdbApiKey}&language={lang}";

        if (page > 1)
        {
            url += $"&page={page}";
        }

        return await _httpClient.GetFromJsonAsync<T>(url);
    }

    public async Task<JsonElement> GetAsync(string path, string? language = null, int page = 1)
    {
        return await GetAsync<JsonElement>(path, language, page);
    }
}
