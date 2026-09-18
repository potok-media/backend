using System.Text.Json;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces.SearchEngine;
using Potok.Backend.Core.Models.SearchEngine;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class ContinueWatchingRepository : IContinueWatchingRepository
{
    private const string Schema = DbSchema.SearchEngine;
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ContinueWatchingRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<TorrentContinueCursor>> ListAsync(int limit, TimeSpan maxAge)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT media_type, tmdb_id, title, poster_src, backdrop_src, still_src, file_index, season, episode,
                   progress_seconds, duration_seconds, audio_name, info_hash, stream::text AS stream_json, updated_at
            FROM {Schema}.continue_watching
            WHERE updated_at > now() - @MaxAge
            ORDER BY updated_at DESC
            LIMIT @Limit";
        var rows = await connection.QueryAsync<ContinueRow>(sql, new { Limit = Math.Clamp(limit, 1, 32), MaxAge = maxAge });
        return rows.Select(Map).ToList();
    }

    public async Task<TorrentContinueCursor?> GetAsync(string mediaType, long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT media_type, tmdb_id, title, poster_src, backdrop_src, still_src, file_index, season, episode,
                   progress_seconds, duration_seconds, audio_name, info_hash, stream::text AS stream_json, updated_at
            FROM {Schema}.continue_watching
            WHERE media_type = @MediaType AND tmdb_id = @TmdbId";
        var row = await connection.QuerySingleOrDefaultAsync<ContinueRow>(sql, new
        {
            MediaType = NormalizeType(mediaType),
            TmdbId = tmdbId,
        });
        return row == null ? null : Map(row);
    }

    public async Task UpsertAsync(TorrentContinueCursor cursor)
    {
        var mediaType = NormalizeType(cursor.MediaType);
        if (string.IsNullOrWhiteSpace(mediaType) || cursor.TmdbId <= 0)
            throw new ArgumentException("mediaType and tmdbId are required");
        if (string.IsNullOrWhiteSpace(cursor.FileIndex))
            throw new ArgumentException("fileIndex is required");

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.continue_watching (
                media_type, tmdb_id, title, poster_src, backdrop_src, still_src, file_index, season, episode,
                progress_seconds, duration_seconds, audio_name, info_hash, stream, updated_at)
            VALUES (
                @MediaType, @TmdbId, @Title, @PosterSrc, @BackdropSrc, @StillSrc, @FileIndex, @Season, @Episode,
                @ProgressSeconds, @DurationSeconds, @AudioName, @InfoHash, @Stream::jsonb, now())
            ON CONFLICT (media_type, tmdb_id) DO UPDATE SET
                title = EXCLUDED.title,
                poster_src = EXCLUDED.poster_src,
                backdrop_src = EXCLUDED.backdrop_src,
                still_src = EXCLUDED.still_src,
                file_index = EXCLUDED.file_index,
                season = EXCLUDED.season,
                episode = EXCLUDED.episode,
                progress_seconds = EXCLUDED.progress_seconds,
                duration_seconds = EXCLUDED.duration_seconds,
                audio_name = EXCLUDED.audio_name,
                info_hash = EXCLUDED.info_hash,
                stream = EXCLUDED.stream,
                updated_at = now()";
        await connection.ExecuteAsync(sql, new
        {
            MediaType = mediaType,
            TmdbId = cursor.TmdbId,
            Title = string.IsNullOrWhiteSpace(cursor.Title) ? "Untitled" : cursor.Title,
            PosterSrc = cursor.PosterSrc,
            BackdropSrc = cursor.BackdropSrc,
            StillSrc = cursor.StillSrc,
            FileIndex = cursor.FileIndex,
            Season = cursor.Season,
            Episode = cursor.Episode,
            ProgressSeconds = Math.Max(0, cursor.ProgressSeconds),
            DurationSeconds = Math.Max(0, cursor.DurationSeconds),
            AudioName = cursor.AudioName,
            InfoHash = string.IsNullOrWhiteSpace(cursor.InfoHash) ? null : cursor.InfoHash.ToLower(),
            Stream = SerializeStream(cursor.Stream),
        });
        await connection.ExecuteAsync(
            $@"DELETE FROM {Schema}.continue_watching WHERE updated_at < now() - interval '90 days'");
    }

    public async Task DeleteAsync(string mediaType, long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"DELETE FROM {Schema}.continue_watching WHERE media_type = @MediaType AND tmdb_id = @TmdbId";
        await connection.ExecuteAsync(sql, new { MediaType = NormalizeType(mediaType), TmdbId = tmdbId });
    }

    private static string NormalizeType(string? mediaType) =>
        string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "tv";

    private static string SerializeStream(object? stream)
    {
        if (stream == null) return "{}";
        if (stream is JsonElement el) return el.ValueKind == JsonValueKind.Undefined ? "{}" : el.GetRawText();
        if (stream is string s && !string.IsNullOrWhiteSpace(s)) return s;
        return JsonSerializer.Serialize(stream, JsonOptions);
    }

    private static TorrentContinueCursor Map(ContinueRow row)
    {
        object? stream = null;
        if (!string.IsNullOrWhiteSpace(row.StreamJson))
        {
            try { stream = JsonSerializer.Deserialize<JsonElement>(row.StreamJson); }
            catch { stream = null; }
        }
        return new TorrentContinueCursor(
            MediaType: row.MediaType,
            TmdbId: row.TmdbId,
            Title: row.Title,
            FileIndex: row.FileIndex,
            ProgressSeconds: row.ProgressSeconds,
            DurationSeconds: row.DurationSeconds,
            PosterSrc: row.PosterSrc,
            BackdropSrc: row.BackdropSrc,
            StillSrc: row.StillSrc,
            Season: row.Season,
            Episode: row.Episode,
            AudioName: row.AudioName,
            InfoHash: row.InfoHash,
            Stream: stream,
            UpdatedAt: row.UpdatedAt);
    }

    private sealed class ContinueRow
    {
        public string MediaType { get; set; } = "tv";
        public long TmdbId { get; set; }
        public string Title { get; set; } = "";
        public string? PosterSrc { get; set; }
        public string? BackdropSrc { get; set; }
        public string? StillSrc { get; set; }
        public string FileIndex { get; set; } = "";
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public int ProgressSeconds { get; set; }
        public int DurationSeconds { get; set; }
        public string? AudioName { get; set; }
        public string? InfoHash { get; set; }
        public string? StreamJson { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
