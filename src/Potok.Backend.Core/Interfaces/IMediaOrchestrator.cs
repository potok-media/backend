using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

public interface IMediaOrchestrator
{
    Task<MediaCard> GetMediaDetailAsync(string mediaType, string id, string? accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> SearchAsync(string query, string baseUrl);
    Task<IEnumerable<MediaCard>> GetPopularMoviesAsync(string baseUrl);
    Task<IEnumerable<MediaCard>> GetPopularTvShowsAsync(string baseUrl);
    Task<MediaSeason?> GetSeasonAsync(string tvId, int seasonNumber, string baseUrl);
}
