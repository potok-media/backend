using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/infuse")]
public class InfuseController : ControllerBase
{
    private readonly IInfuseRepository _repository;
    private readonly ITorrServerClient _torrServerClient;

    public InfuseController(IInfuseRepository repository, ITorrServerClient torrServerClient)
    {
        _repository = repository;
        _torrServerClient = torrServerClient;
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems()
    {
        var items = await _repository.GetAllAsync();
        return Ok(items);
    }

    [HttpDelete("items/{id}")]
    public async Task<IActionResult> DeleteItem(Guid id)
    {
        await _repository.DeleteAsync(id);
        return Ok();
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveAndGetStreamUrls([FromBody] TorrentFilesRequest request)
    {
        // 1. Get the stream URLs to return to the client
        var streamUrls = await _torrServerClient.GetNormalizedStreamUrlsAsync(request);
        var urlsList = streamUrls.ToList();
        
        if (!urlsList.Any()) return BadRequest("No files found or unable to fetch streams.");

        // 2. Extract hash from magnet (basic parsing)
        var hash = string.Empty;
        if (!string.IsNullOrEmpty(request.MagnetUri) && request.MagnetUri.Contains("urn:btih:"))
        {
            var parts = request.MagnetUri.Split("urn:btih:");
            if (parts.Length > 1) hash = parts[1].Split('&')[0].ToLower();
        }

        // 3. Save or update item
        if (!request.TmdbId.HasValue) return Ok(urlsList);

        var existing = await _repository.GetByTmdbIdAsync(request.TmdbId.Value);
        var item = existing ?? new InfuseLibraryItem
        {
            Id = Guid.NewGuid(),
            TmdbId = request.TmdbId.Value,
            CreatedAt = DateTime.UtcNow
        };

        item.MediaType = request.MediaType;
        item.Title = request.OriginalTitle ?? request.EnglishTitle ?? "Unknown";
        item.Poster = request.Poster ?? string.Empty;
        item.TorrentTitle = request.Title ?? string.Empty;
        item.TorrentHash = hash;
        item.MagnetUri = request.MagnetUri ?? string.Empty;
        item.Link = request.Link ?? string.Empty;
        item.Status = InfuseItemStatus.Active;
        item.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveAsync(item);

        return Ok(urlsList);
    }
}
