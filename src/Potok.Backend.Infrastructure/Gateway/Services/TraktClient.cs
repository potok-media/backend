using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Potok.Backend.Core.Models;
using ILogger = Serilog.ILogger;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TraktClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private const string TraktApiBase = "https://api.trakt.tv";

    public TraktClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(TraktApiBase);
    }

    public async Task<TraktWatchProgress?> GetWatchedProgressAsync(string mediaType, long tmdbId, string accessToken)
    {
        try
        {
            _logger.Debug("Fetching watched progress for {MediaType} TMDB ID: {TmdbId}", mediaType, tmdbId);
            
            // 1. Find Trakt ID by TMDB ID
            var searchUrl = $"search/tmdb/{tmdbId}?type={mediaType}";
            var searchResponse = await _httpClient.GetFromJsonAsync<List<TraktSearchResult>>(searchUrl);
            
            var traktResult = searchResponse?.FirstOrDefault();
            if (mediaType == "movie")
            {
                var movie = traktResult?.Movie;
                if (movie?.Ids?.Trakt == null) return null;
                
                // For movies, we check the history to see if there are any plays
                var historyUrl = $"sync/history/movies/{movie.Ids.Trakt}";
                var history = await SendRequestAsync<List<TraktHistoryItem>>(HttpMethod.Get, historyUrl, accessToken);
                
                if (history != null && history.Any())
                {
                    return new TraktWatchProgress(Aired: 1, Completed: 1, LastEpisode: null, NextEpisode: null, Seasons: null);
                }
                return new TraktWatchProgress(Aired: 1, Completed: 0, LastEpisode: null, NextEpisode: null, Seasons: null);
            }
            else
            {
                var traktShow = traktResult?.Show;
                if (traktShow?.Ids?.Slug == null || traktShow?.Ids?.Trakt == null) 
                {
                    _logger.Warning("Could not find Trakt show for TMDB ID: {TmdbId}", tmdbId);
                    return null;
                }

                var progressTask = GetWatchedProgressBySlugAsync(traktShow.Ids.Slug, accessToken);
                var historyTask = GetShowHistoryAsync(traktShow.Ids.Trakt, accessToken);

                await Task.WhenAll(progressTask, historyTask);

                var progress = await progressTask;
                var history = await historyTask;

                if (progress == null) return null;
                if (history == null || !history.Any()) return progress;

                // Merge history into progress to bypass Trakt's progress/watched cache
                var historyBySeason = history
                    .Where(h => h.Episode != null)
                    .GroupBy(h => h.Episode!.Season)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(h => h.Episode!.Number).Distinct().ToList()
                    );

                var newSeasons = new List<TraktSeasonProgress>();
                var totalCompleted = 0;

                foreach (var seasonProgress in progress.Seasons ?? Enumerable.Empty<TraktSeasonProgress>())
                {
                    var episodes = seasonProgress.Episodes?.ToList() ?? new List<TraktEpisodeProgress>();
                    if (historyBySeason.TryGetValue(seasonProgress.Number, out var historyEpisodes))
                    {
                        foreach (var epNum in historyEpisodes)
                        {
                            if (!episodes.Any(e => e.Number == epNum))
                            {
                                episodes.Add(new TraktEpisodeProgress(epNum, true));
                            }
                        }
                    }
                    
                    var completedCount = episodes.Count(e => e.Completed);
                    totalCompleted += completedCount;
                    
                    newSeasons.Add(seasonProgress with { 
                        Episodes = episodes,
                        Completed = completedCount
                    });
                    
                    historyBySeason.Remove(seasonProgress.Number);
                }

                // Add seasons that might be in history but not in progress
                foreach (var kvp in historyBySeason)
                {
                    var episodes = kvp.Value.Select(num => new TraktEpisodeProgress(num, true)).ToList();
                    newSeasons.Add(new TraktSeasonProgress(
                        Number: kvp.Key,
                        Aired: episodes.Count, 
                        Completed: episodes.Count,
                        Episodes: episodes
                    ));
                    totalCompleted += episodes.Count;
                }

                return progress with {
                    Seasons = newSeasons.OrderBy(s => s.Number).ToList(),
                    Completed = totalCompleted
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error fetching watched progress for {MediaType} TMDB ID: {TmdbId}", mediaType, tmdbId);
            return null;
        }
    }

    public async Task<TraktMetadata?> GetMediaMetadataAsync(string mediaType, long tmdbId, string accessToken)
    {
        try
        {
            // We use the 'hidden' metadata/collection sync endpoints or just check specific lists
            // A more efficient way is to check the user's collection, watchlist, and favorites for this ID
            // But for simplicity and accuracy per-item:
            var watchlist = await GetWatchlistAsync(accessToken);
            var favorites = await GetFavoritesAsync(accessToken);

            bool inWatchlist = watchlist?.Any(i => 
                (mediaType == "movie" && i.Movie?.Ids?.Tmdb == tmdbId) || 
                (mediaType == "tv" && i.Show?.Ids?.Tmdb == tmdbId)
            ) ?? false;

            bool inFavorites = favorites?.Any(i => 
                (mediaType == "movie" && i.Movie?.Ids?.Tmdb == tmdbId) || 
                (mediaType == "tv" && i.Show?.Ids?.Tmdb == tmdbId)
            ) ?? false;

            return new TraktMetadata(inWatchlist, inFavorites);
        }
        catch (Exception)
        {
            return new TraktMetadata(false, false);
        }
    }

    public async Task<TraktWatchProgress?> GetWatchedProgressBySlugAsync(string slug, string accessToken)
    {
        return await SendRequestAsync<TraktWatchProgress>(HttpMethod.Get, $"shows/{slug}/progress/watched", accessToken);
    }

    public async Task<List<TraktListItem>?> GetWatchlistAsync(string accessToken)
    {
        return await SendRequestAsync<List<TraktListItem>>(HttpMethod.Get, "sync/watchlist?extended=full", accessToken);
    }

    public async Task<List<TraktListItem>?> GetFavoritesAsync(string accessToken)
    {
        return await SendRequestAsync<List<TraktListItem>>(HttpMethod.Get, "sync/favorites?extended=full", accessToken);
    }

    public async Task<List<TraktHistoryItem>?> GetHistoryAsync(string accessToken)
    {
        return await SendRequestAsync<List<TraktHistoryItem>>(HttpMethod.Get, "sync/history?extended=full&limit=100", accessToken);
    }

    public async Task<List<TraktHistoryItem>?> GetShowHistoryAsync(int traktId, string accessToken)
    {
        return await SendRequestAsync<List<TraktHistoryItem>>(HttpMethod.Get, $"sync/history/shows/{traktId}?extended=full&limit=1000", accessToken);
    }

    public async Task<List<TraktCalendarItem>?> GetCalendarAsync(string accessToken)
    {
        var start = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return await SendRequestAsync<List<TraktCalendarItem>>(HttpMethod.Get, $"calendars/my/shows/{start}/30?extended=full", accessToken);
    }

    public async Task<List<TraktWatchedShow>?> GetWatchedShowsAsync(string accessToken)
    {
        return await SendRequestAsync<List<TraktWatchedShow>>(HttpMethod.Get, "sync/watched/shows?extended=full", accessToken);
    }

    public async Task<List<TraktWatchedMovie>?> GetWatchedMoviesAsync(string accessToken)
    {
        return await SendRequestAsync<List<TraktWatchedMovie>>(HttpMethod.Get, "sync/watched/movies?extended=full", accessToken);
    }

    public async Task<TraktShowProgress?> GetShowProgressAsync(string slug, string accessToken)
    {
        return await SendRequestAsync<TraktShowProgress>(HttpMethod.Get, $"shows/{slug}/progress/watched", accessToken);
    }

    public async Task<TraktUserProfile?> GetUserProfileAsync(string accessToken)
    {
        return await SendRequestAsync<TraktUserProfile>(HttpMethod.Get, "users/me?extended=full", accessToken);
    }

    public async Task<TraktUserStats?> GetUserStatsAsync(string accessToken)
    {
        return await SendRequestAsync<TraktUserStats>(HttpMethod.Get, "users/me/stats", accessToken);
    }

    private async Task<T?> SendRequestAsync<T>(HttpMethod method, string path, string accessToken)
    {
        try
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.Error("Failed Trakt request to {Path}: {StatusCode}. Body: {ErrorBody}", path, response.StatusCode, errorBody);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in Trakt SendRequestAsync for {Path}", path);
            return default;
        }
    }
}

public record TraktSearchResult(
    [property: JsonPropertyName("show")] TraktShow? Show,
    [property: JsonPropertyName("movie")] TraktMovie? Movie
);

public record TraktMetadata(bool InWatchlist, bool InFavorites);
