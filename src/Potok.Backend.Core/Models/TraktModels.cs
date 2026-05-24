using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models;

public record TraktWatchProgress(
    [property: JsonPropertyName("aired")] int Aired,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("last_episode")] TraktEpisode? LastEpisode,
    [property: JsonPropertyName("next_episode")] TraktEpisode? NextEpisode,
    [property: JsonPropertyName("seasons")] List<TraktSeasonProgress>? Seasons
);

public record TraktSeasonProgress(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("aired")] int Aired,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("episodes")] List<TraktEpisodeProgress>? Episodes
);

public record TraktEpisodeProgress(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("completed")] bool Completed
);

public record TraktEpisode(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktIds? Ids = null
);

public record TraktIds(
    [property: JsonPropertyName("trakt")] int Trakt,
    [property: JsonPropertyName("slug")] string? Slug = null,
    [property: JsonPropertyName("tvdb")] int? Tvdb = null,
    [property: JsonPropertyName("imdb")] string? Imdb = null,
    [property: JsonPropertyName("tmdb")] int? Tmdb = null
);

public record TraktMovie(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("ids")] TraktIds? Ids
);

public record TraktShow(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("ids")] TraktIds? Ids
);

public record TraktListItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("movie")] TraktMovie? Movie,
    [property: JsonPropertyName("show")] TraktShow? Show
);

public record TraktHistoryItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("watched_at")] DateTime? WatchedAt,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("movie")] TraktMovie? Movie,
    [property: JsonPropertyName("show")] TraktShow? Show,
    [property: JsonPropertyName("episode")] TraktEpisode? Episode
);

public record TraktCalendarItem(
    [property: JsonPropertyName("first_aired")] DateTime? FirstAired,
    [property: JsonPropertyName("episode")] TraktEpisode? Episode,
    [property: JsonPropertyName("show")] TraktShow? Show
);

public record TraktWatchedShow(
    [property: JsonPropertyName("plays")] int Plays,
    [property: JsonPropertyName("last_watched_at")] DateTime? LastWatchedAt,
    [property: JsonPropertyName("show")] TraktShow? Show
);

public record TraktWatchedMovie(
    [property: JsonPropertyName("plays")] int Plays,
    [property: JsonPropertyName("last_watched_at")] DateTime? LastWatchedAt,
    [property: JsonPropertyName("movie")] TraktMovie? Movie
);

public record TraktShowProgress(
    [property: JsonPropertyName("aired")] int Aired,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("next_episode")] TraktEpisode? NextEpisode,
    [property: JsonPropertyName("last_episode")] TraktEpisode? LastEpisode
);

public record TraktUserAvatar(
    [property: JsonPropertyName("full")] string? Full
);

public record TraktUserImages(
    [property: JsonPropertyName("avatar")] TraktUserAvatar? Avatar
);

public record TraktUserProfile(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("vip")] bool Vip,
    [property: JsonPropertyName("vip_ep")] bool VipEp,
    [property: JsonPropertyName("images")] TraktUserImages? Images
);

public record TraktMovieStats(
    [property: JsonPropertyName("watched")] int Watched,
    [property: JsonPropertyName("plays")] int Plays,
    [property: JsonPropertyName("minutes")] int Minutes
);

public record TraktEpisodeStats(
    [property: JsonPropertyName("watched")] int Watched,
    [property: JsonPropertyName("plays")] int Plays,
    [property: JsonPropertyName("minutes")] int Minutes
);

public record TraktRatingStats(
    [property: JsonPropertyName("total")] int Total
);

public record TraktUserStats(
    [property: JsonPropertyName("movies")] TraktMovieStats? Movies,
    [property: JsonPropertyName("episodes")] TraktEpisodeStats? Episodes,
    [property: JsonPropertyName("ratings")] TraktRatingStats? Ratings
);

public record UserProfileResponse(
    string Username,
    string? Name,
    bool IsVip,
    string? AvatarUrl,
    int MoviesWatched,
    int EpisodesWatched,
    int TotalWatchMinutes,
    int RatingsCount
);
