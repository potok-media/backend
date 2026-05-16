using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

public interface IMediaOrchestrator
{
    Task<MediaCard> GetMediaDetailAsync(string mediaType, long id, string? accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> SearchAsync(string query, string baseUrl);
    Task<IEnumerable<MediaCard>> GetPopularMoviesAsync(string baseUrl);
    Task<IEnumerable<MediaCard>> GetPopularTvShowsAsync(string baseUrl);
    Task<IEnumerable<MediaCard>> GetMediaRowAsync(string rowId, int page, string baseUrl);
    Task<MediaSeason?> GetSeasonAsync(long tvId, int seasonNumber, string baseUrl);
}
