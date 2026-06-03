using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Mappers;
using Potok.Backend.Core.Models;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class LibraryOrchestrator : ILibraryOrchestrator
{
    private readonly TraktClient _traktClient;
    private readonly TmdbClient _tmdbClient;

    public LibraryOrchestrator(TraktClient traktClient, TmdbClient tmdbClient)
    {
        _traktClient = traktClient;
        _tmdbClient = tmdbClient;
    }

    public async Task<IEnumerable<MediaCard>> GetWatchlistAsync(string accessToken, string baseUrl)
    {
        var items = await _traktClient.GetWatchlistAsync(accessToken);
        return await EnrichItemsAsync(items, baseUrl);
    }

    public async Task<IEnumerable<MediaCard>> GetFavoritesAsync(string accessToken, string baseUrl)
    {
        var items = await _traktClient.GetFavoritesAsync(accessToken);
        return await EnrichItemsAsync(items, baseUrl);
    }

    public async Task<IEnumerable<MediaCard>> GetHistoryAsync(string accessToken, string baseUrl)
    {
        var showsTask = _traktClient.GetWatchedShowsAsync(accessToken);
        var moviesTask = _traktClient.GetWatchedMoviesAsync(accessToken);

        await Task.WhenAll(showsTask, moviesTask);

        var shows = await showsTask ?? Enumerable.Empty<TraktWatchedShow>();
        var movies = await moviesTask ?? Enumerable.Empty<TraktWatchedMovie>();

        var unifiedHistory = shows.Select(s => new { 
                Type = "show", 
                LastWatchedAt = s.LastWatchedAt, 
                TmdbId = s.Show?.Ids?.Tmdb 
            })
            .Concat(movies.Select(m => new { 
                Type = "movie", 
                LastWatchedAt = m.LastWatchedAt, 
                TmdbId = m.Movie?.Ids?.Tmdb 
            }))
            .Where(x => x.TmdbId != null)
            .OrderByDescending(x => x.LastWatchedAt)
            .Take(50)
            .ToList();

        if (!unifiedHistory.Any()) return Enumerable.Empty<MediaCard>();

        var tasks = unifiedHistory.Select(async item => {
            if (item.Type == "movie") {
                var tmdb = await _tmdbClient.GetAsync<TmdbMovie>($"movie/{item.TmdbId}");
                return tmdb != null ? MediaMapper.MapToMediaCard(tmdb, baseUrl) : null;
            } else {
                var tmdb = await _tmdbClient.GetAsync<TmdbTvShow>($"tv/{item.TmdbId}");
                return tmdb != null ? MediaMapper.MapToMediaCard(tmdb, baseUrl) : null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
    }

    public async Task<IEnumerable<MediaCard>> GetCalendarAsync(string? accessToken, string baseUrl)
    {
        var items = await _traktClient.GetCalendarAsync(accessToken);
        if (items == null) return Enumerable.Empty<MediaCard>();

        var tasks = items.Take(30).Select(async i => {
            if (i.Show?.Ids?.Tmdb != null) {
                var tmdb = await _tmdbClient.GetAsync<TmdbTvShow>($"tv/{i.Show.Ids.Tmdb}");
                if (tmdb != null) {
                    var card = MediaMapper.MapToMediaCard(tmdb, baseUrl);
                    var airDate = i.FirstAired?.ToLocalTime().ToString("dd.MM") ?? "Скоро";
                    return card with { 
                        BadgeText = $"{airDate}: S{i.Episode?.Season:D2}E{i.Episode?.Number:D2}",
                        NextEpisodeNumber = i.Episode?.Number,
                        NextEpisodeSeason = i.Episode?.Season,
                        NextEpisodeTitle = i.Episode?.Title,
                        AirDateTime = i.FirstAired?.ToLocalTime()
                    };
                }
            }
            return null;
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
    }

    public async Task<IEnumerable<MediaCard>> GetUpNextAsync(string accessToken, string baseUrl)
    {
        var watchedShows = await _traktClient.GetWatchedShowsAsync(accessToken);
        if (watchedShows == null) return Enumerable.Empty<MediaCard>();

        // Sort by last watched to show active shows first
        var tasks = watchedShows
            .OrderByDescending(s => s.LastWatchedAt)
            .Take(30)
            .Select(async s => {
                if (s.Show?.Ids?.Slug != null && s.Show.Ids.Tmdb != null) {
                    var progress = await _traktClient.GetShowProgressAsync(s.Show.Ids.Slug, accessToken);
                    if (progress?.NextEpisode != null) {
                        var tmdb = await _tmdbClient.GetAsync<TmdbTvShow>($"tv/{s.Show.Ids.Tmdb}");
                        if (tmdb != null) {
                            var card = MediaMapper.MapToMediaCard(tmdb, baseUrl);
                            return card with { 
                                NextEpisodeNumber = progress.NextEpisode.Number,
                                NextEpisodeSeason = progress.NextEpisode.Season,
                                NextEpisodeTitle = progress.NextEpisode.Title,
                                Progress = new WatchProgress(
                                    Aired: progress.Aired,
                                    Completed: progress.Completed,
                                    NextSeason: progress.NextEpisode.Season,
                                    NextEpisode: progress.NextEpisode.Number
                                )
                            };
                        }
                    }
                }
                return null;
            });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
    }

    private async Task<IEnumerable<MediaCard>> EnrichItemsAsync(IEnumerable<TraktListItem>? items, string baseUrl)
    {
        if (items == null) return Enumerable.Empty<MediaCard>();

        var tasks = items.Take(50).Select(async i => {
            if (i.Type == "movie" && i.Movie?.Ids?.Tmdb != null) {
                var tmdb = await _tmdbClient.GetAsync<TmdbMovie>($"movie/{i.Movie.Ids.Tmdb}");
                return tmdb != null ? MediaMapper.MapToMediaCard(tmdb, baseUrl) : null;
            }
            if (i.Type == "show" && i.Show?.Ids?.Tmdb != null) {
                var tmdb = await _tmdbClient.GetAsync<TmdbTvShow>($"tv/{i.Show.Ids.Tmdb}");
                return tmdb != null ? MediaMapper.MapToMediaCard(tmdb, baseUrl) : null;
            }
            return null;
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
    }

    public async Task<UserProfileResponse> GetUserProfileAsync(string accessToken)
    {
        var profileTask = _traktClient.GetUserProfileAsync(accessToken);
        var statsTask = _traktClient.GetUserStatsAsync(accessToken);

        await Task.WhenAll(profileTask, statsTask);

        var profile = await profileTask;
        var stats = await statsTask;

        if (profile == null)
        {
            throw new Exception("Failed to retrieve Trakt profile");
        }

        var username = profile.Username;
        var name = profile.Name;
        var isVip = profile.Vip || profile.VipEp;
        var avatarUrl = profile.Images?.Avatar?.Full;

        var moviesWatched = stats?.Movies?.Watched ?? 0;
        var episodesWatched = stats?.Episodes?.Watched ?? 0;
        var totalWatchMinutes = (stats?.Movies?.Minutes ?? 0) + (stats?.Episodes?.Minutes ?? 0);
        var ratingsCount = stats?.Ratings?.Total ?? 0;

        return new UserProfileResponse(
            Username: username,
            Name: name,
            IsVip: isVip,
            AvatarUrl: avatarUrl,
            MoviesWatched: moviesWatched,
            EpisodesWatched: episodesWatched,
            TotalWatchMinutes: totalWatchMinutes,
            RatingsCount: ratingsCount
        );
    }
}
