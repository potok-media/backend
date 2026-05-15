using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class MediaOrchestrator : IMediaOrchestrator
{
    private readonly TmdbClient _tmdbClient;
    private readonly TraktClient _traktClient;

    public MediaOrchestrator(TmdbClient tmdbClient, TraktClient traktClient)
    {
        _tmdbClient = tmdbClient;
        _traktClient = traktClient;
    }

    public async Task<MediaCard> GetMediaDetailAsync(string mediaType, string id, string? accessToken, string baseUrl)
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
            Console.WriteLine($"[MediaOrchestrator] Access token present, requesting Trakt progress/metadata for {mediaType} {id}");
            progressTask = _traktClient.GetWatchedProgressAsync(mediaType, id, accessToken);
            metadataTask = _traktClient.GetMediaMetadataAsync(mediaType, id, accessToken);
        }
        else
        {
            Console.WriteLine($"[MediaOrchestrator] No access token, skipping Trakt data for {mediaType} {id}");
        }

        await Task.WhenAll(movieTask, tvTask, progressTask, metadataTask);

        MediaCard? card = null;

        if (mediaType == "movie")
        {
            var movie = await movieTask;
            if (movie != null)
            {
                card = MediaMapper.MapToMediaCard(movie, baseUrl);
            }
        }
        else
        {
            var tv = await tvTask;
            if (tv != null)
            {
                card = MediaMapper.MapToMediaCard(tv, baseUrl);
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
        
        return card;
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
                results.Add(MediaMapper.MapToMediaCard(movieTask.Result, baseUrl));
            }
            if (tvTask.Result != null && !string.IsNullOrEmpty(tvTask.Result.Name)) 
            {
                results.Add(MediaMapper.MapToMediaCard(tvTask.Result, baseUrl));
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

    public async Task<MediaSeason?> GetSeasonAsync(string tvId, int seasonNumber, string baseUrl)
    {
        var path = $"tv/{tvId}/season/{seasonNumber}?append_to_response=credits";
        var season = await _tmdbClient.GetAsync<TmdbSeason>(path);
        return season != null ? MediaMapper.MapToMediaSeason(season, baseUrl) : null;
    }
}
