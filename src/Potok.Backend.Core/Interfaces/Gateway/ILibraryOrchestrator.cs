using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface ILibraryOrchestrator
{
    Task<IEnumerable<MediaCard>> GetWatchlistAsync(string accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> GetFavoritesAsync(string accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> GetHistoryAsync(string accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> GetCalendarAsync(string? accessToken, string baseUrl);
    Task<IEnumerable<MediaCard>> GetUpNextAsync(string accessToken, string baseUrl);
    Task<UserProfileResponse> GetUserProfileAsync(string accessToken);
}
