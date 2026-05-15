using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
public class TmdbProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayOptions _options;
    private const string TmdbApiBase = "https://api.themoviedb.org/3";

    public TmdbProxyController(IHttpClientFactory httpClientFactory, IOptions<GatewayOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    [HttpGet("api/tmdb/{*path}")]
    public async Task<IActionResult> ProxyApi(string path)
    {
        var client = _httpClientFactory.CreateClient();
        var qs = Request.QueryString.Value;
        var separator = string.IsNullOrEmpty(qs) ? "?" : "&";
        var url = $"{TmdbApiBase}/{path}{qs}{separator}api_key={_options.TmdbApiKey}";

        var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        
        // Forward relevant headers
        if (Request.Headers.TryGetValue("Accept-Language", out var lang))
        {
            requestMessage.Headers.AcceptLanguage.ParseAdd(lang!);
        }

        var response = await client.SendAsync(requestMessage);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, errorContent);
        }

        var content = await response.Content.ReadAsStreamAsync();
        return File(content, response.Content.Headers.ContentType?.ToString() ?? "application/json");
    }

    [HttpGet("media/tmdb/{*path}")]
    [ResponseCache(Duration = 2592000, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> ProxyMedia(string path)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://image.tmdb.org/{path}";
        
        var response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode);
        }

        var content = await response.Content.ReadAsStreamAsync();

        return File(content, response.Content.Headers.ContentType?.ToString() ?? "image/jpeg");
    }
}
