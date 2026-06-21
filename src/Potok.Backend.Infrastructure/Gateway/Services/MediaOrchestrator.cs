using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class MediaOrchestrator : IMediaOrchestrator
{
    private readonly TmdbClient _tmdbClient;
    private readonly TraktClient _traktClient;
    private readonly ICacheService _cacheService;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;

    public MediaOrchestrator(TmdbClient tmdbClient, TraktClient traktClient, ICacheService cacheService, System.Net.Http.IHttpClientFactory httpClientFactory)
    {
        _tmdbClient = tmdbClient;
        _traktClient = traktClient;
        _cacheService = cacheService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<MediaCard> GetMediaDetailAsync(string mediaType, long id, string? accessToken, string baseUrl)
    {
        var tmdbPath = $"{mediaType}/{id}?append_to_response=content_ratings,release_dates,external_ids,images,translations,credits&include_image_language=ru,en,null";
        
        Task<TmdbMovie?> movieTask = Task.FromResult<TmdbMovie?>(null);
        Task<TmdbTvShow?> tvTask = Task.FromResult<TmdbTvShow?>(null);
        Task<TraktWatchProgress?> progressTask = Task.FromResult<TraktWatchProgress?>(null);
        Task<TraktMetadata?> metadataTask = Task.FromResult<TraktMetadata?>(null);

        if (mediaType == "movie")
        {
            movieTask = _tmdbClient.GetAsync<TmdbMovie>(tmdbPath);
        }
        else
        {
            tvTask = _tmdbClient.GetAsync<TmdbTvShow>(tmdbPath);
        }

        if (!string.IsNullOrEmpty(accessToken))
        {
            progressTask = _traktClient.GetWatchedProgressAsync(mediaType, id, accessToken);
            metadataTask = _traktClient.GetMediaMetadataAsync(mediaType, id, accessToken);
        }

        await Task.WhenAll(movieTask, tvTask, progressTask, metadataTask);

        MediaCard? card = null;

        if (mediaType == "movie")
        {
            var movie = await movieTask;
            if (movie != null)
            {
                card = MediaMapper.MapToMediaCard(movie, baseUrl, language: _tmdbClient.CurrentLanguage);
            }
        }
        else
        {
            var tv = await tvTask;
            if (tv != null)
            {
                card = MediaMapper.MapToMediaCard(tv, baseUrl, language: _tmdbClient.CurrentLanguage);
            }
        }

        if (card == null) return null!;

        // Apply Metadata (Watchlist/Favorite)
        var metadata = await metadataTask;
        if (metadata != null)
        {
            card = card with { 
                IsInWatchlist = metadata.InWatchlist,
                IsFavorite = metadata.InFavorites
            };
        }

        var progress = await progressTask;
        if (progress != null)
        {
            var watchedEpisodes = progress.Seasons?
                .SelectMany(s => (s.Episodes ?? new List<TraktEpisodeProgress>())
                    .Where(e => e.Completed)
                    .Select(e => new WatchedEpisode(s.Number, e.Number)))
                .ToList();

            card = card with
            {
                Progress = new WatchProgress(
                    Aired: progress.Aired,
                    Completed: progress.Completed,
                    LastEpisodeTitle: progress.LastEpisode?.Title,
                    LastSeason: progress.LastEpisode?.Season,
                    LastEpisode: progress.LastEpisode?.Number,
                    NextEpisodeTitle: progress.NextEpisode?.Title,
                    NextSeason: progress.NextEpisode?.Season,
                    NextEpisode: progress.NextEpisode?.Number,
                    WatchedEpisodes: watchedEpisodes
                )
            };
        }
        
        if (card != null)
        {
            var kpId = await GetKpIdAsync(card.Id, card.ImdbId);
            card = card with { KpId = kpId };
        }
        
        return card!;
    }

    public async Task<IEnumerable<MediaCard>> SearchAsync(string query, string baseUrl)
    {
        var cleanQuery = query.Trim();
        if (cleanQuery.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring(5).Trim();
        }
        
        if (int.TryParse(cleanQuery, out var id))
        {
            var movieTask = _tmdbClient.GetAsync<TmdbMovie>($"movie/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
            var tvTask = _tmdbClient.GetAsync<TmdbTvShow>($"tv/{id}?append_to_response=images,translations&include_image_language=ru,en,null");
            
            await Task.WhenAll(movieTask, tvTask);
            var results = new List<MediaCard>();
            
            if (movieTask.Result != null && !string.IsNullOrEmpty(movieTask.Result.Title)) 
            {
                results.Add(MediaMapper.MapToMediaCard(movieTask.Result, baseUrl, language: _tmdbClient.CurrentLanguage));
            }
            if (tvTask.Result != null && !string.IsNullOrEmpty(tvTask.Result.Name))
            {
                results.Add(MediaMapper.MapToMediaCard(tvTask.Result, baseUrl, language: _tmdbClient.CurrentLanguage));
            }
            
            if (results.Any()) return results;
        }

        var path = $"search/multi?query={Uri.EscapeDataString(query)}";
        var response = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMultiSearchResult>>(path);
        
        return response?.Results
            .Select(item => MediaMapper.MapToMediaCard(item, baseUrl)) 
            ?? Enumerable.Empty<MediaCard>();
    }

    public async Task<IEnumerable<MediaCard>> GetPopularMoviesAsync(string baseUrl)
    {
        var response = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMovie>>("movie/popular");
        return response?.Results
            .Select(m => MediaMapper.MapToMediaCard(m, baseUrl)) 
            ?? Enumerable.Empty<MediaCard>();
    }

    public async Task<IEnumerable<MediaCard>> GetPopularTvShowsAsync(string baseUrl)
    {
        var response = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbTvShow>>("tv/popular");
        return response?.Results
            .Select(t => MediaMapper.MapToMediaCard(t, baseUrl)) 
            ?? Enumerable.Empty<MediaCard>();
    }

    public async Task<MediaSeason?> GetSeasonAsync(long tvId, int seasonNumber, string baseUrl)
    {
        var path = $"tv/{tvId}/season/{seasonNumber}?append_to_response=credits";
        var season = await _tmdbClient.GetAsync<TmdbSeason>(path);
        return season != null ? MediaMapper.MapToMediaSeason(season, baseUrl) : null;
    }

    private static readonly Dictionary<string, (string Path, string MediaType)> RowDefinitions = new()
    {
        ["movie.now-playing"] = ("movie/now_playing", "movie"),
        ["movie.trending-day"] = ("trending/movie/day", "movie"),
        ["movie.trending-week"] = ("trending/movie/week", "movie"),
        ["movie.upcoming"] = ("movie/upcoming", "movie"),
        ["movie.popular"] = ("movie/popular", "movie"),
        ["tv.popular"] = ("trending/tv/week", "tv"),
        ["movie.top-rated"] = ("movie/top_rated", "movie"),
        ["tv.top-rated"] = ("tv/top_rated", "tv")
    };

    public async Task<IEnumerable<MediaCard>> GetMediaRowAsync(string rowId, int page, string baseUrl)
    {
        string? tmdbPath = null;
        string? mediaType = null;

        if (RowDefinitions.TryGetValue(rowId, out var def))
        {
            tmdbPath = def.Path;
            mediaType = def.MediaType;
        }
        else if (rowId.StartsWith("genre.", StringComparison.OrdinalIgnoreCase))
        {
            // Format: genre.movie.28 or genre.tv.16
            var parts = rowId.Split('.');
            if (parts.Length == 3)
            {
                mediaType = parts[1];
                var genreId = parts[2];
                tmdbPath = $"discover/{mediaType}?with_genres={genreId}";
            }
        }

        if (tmdbPath == null) return Enumerable.Empty<MediaCard>();

        var response = await _tmdbClient.GetAsync<TmdbPagedResponse<TmdbMultiSearchResult>>(tmdbPath, page: page);
        
        return response?.Results?
            .Select(item => MediaMapper.MapToMediaCard(item with { MediaType = mediaType ?? item.MediaType }, baseUrl)) 
            ?? Enumerable.Empty<MediaCard>();
    }

    private async Task<string?> GetKpIdAsync(long tmdbId, string? imdbId)
    {
        var cacheKey = $"potok:kp_id:{tmdbId}";
        return await _cacheService.GetOrCreateAsync(
            cacheKey,
            async () => {
                try
                {
                    var httpClient = _httpClientFactory.CreateClient("Default");
                    httpClient.Timeout = TimeSpan.FromSeconds(5);
                    var token = "04941a9a3ca3ac16e2b4327347bbc1";
                    
                    // Try tmdbId lookup first
                    var url = $"http://api.alloha.tv/?token={token}&tmdb={tmdbId}";
                    var response = await httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                        var data = obj?.Value<Newtonsoft.Json.Linq.JObject>("data");
                        if (data != null)
                        {
                            var kp = data.Value<string>("id_kp") ?? data.Value<string>("kp");
                            if (!string.IsNullOrEmpty(kp) && kp != "0") return kp;
                        }
                    }
                    
                    // Fallback to imdbId if available
                    if (!string.IsNullOrEmpty(imdbId))
                    {
                        url = $"https://api.alloha.tv/?token={token}&imdb={imdbId}";
                        response = await httpClient.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                            var data = obj?.Value<Newtonsoft.Json.Linq.JObject>("data");
                            if (data != null)
                            {
                                var kp = data.Value<string>("id_kp") ?? data.Value<string>("kp");
                                if (!string.IsNullOrEmpty(kp) && kp != "0") return kp;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[MediaOrchestrator] GetKpIdAsync failed: " + ex);
                }
                return null;
            },
            TimeSpan.FromDays(7)
        );
    }
}
