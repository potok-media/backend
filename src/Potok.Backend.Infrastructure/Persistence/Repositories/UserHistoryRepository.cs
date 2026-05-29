using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class UserHistoryRepository : IUserHistoryRepository
{
    private const string Schema = DbSchema.Gateway;
    private readonly string _connectionString;

    public UserHistoryRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IEnumerable<UserHistory>> GetByUserIdAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT id, user_id as UserId, tmdb_id as TmdbId, media_type as MediaType, 
                   season_number as SeasonNumber, episode_number as EpisodeNumber, 
                   progress_seconds as ProgressSeconds, duration_seconds as DurationSeconds, 
                   last_watched_at as LastWatchedAt 
            FROM {Schema}.user_history 
            WHERE user_id = @UserId 
            ORDER BY last_watched_at DESC";
        return await connection.QueryAsync<UserHistory>(sql, new { UserId = userId });
    }

    public async Task<UserHistory?> GetProgressAsync(Guid userId, string tmdbId, string mediaType, int? season = null, int? episode = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT id, user_id as UserId, tmdb_id as TmdbId, media_type as MediaType, 
                   season_number as SeasonNumber, episode_number as EpisodeNumber, 
                   progress_seconds as ProgressSeconds, duration_seconds as DurationSeconds, 
                   last_watched_at as LastWatchedAt 
            FROM {Schema}.user_history 
            WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType 
              AND (season_number IS NULL OR season_number = @Season)
              AND (episode_number IS NULL OR episode_number = @Episode)";
        return await connection.QuerySingleOrDefaultAsync<UserHistory>(sql, new
        {
            UserId = userId,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Season = season,
            Episode = episode
        });
    }

    public async Task SaveProgressAsync(UserHistory history)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.user_history 
                (id, user_id, tmdb_id, media_type, season_number, episode_number, progress_seconds, duration_seconds, last_watched_at) 
            VALUES 
                (@Id, @UserId, @TmdbId, @MediaType, @SeasonNumber, @EpisodeNumber, @ProgressSeconds, @DurationSeconds, @LastWatchedAt)
            ON CONFLICT (user_id, tmdb_id, media_type) 
            DO UPDATE SET 
                progress_seconds = EXCLUDED.progress_seconds,
                duration_seconds = EXCLUDED.duration_seconds,
                last_watched_at = EXCLUDED.last_watched_at,
                season_number = EXCLUDED.season_number,
                episode_number = EXCLUDED.episode_number";
        
        // Note: For ON CONFLICT to work properly with nullable season/episode, we have the unique index
        // or we can delete and re-insert if needed, but since we defined a unique constraint or index:
        // Let's check our index ix_user_history_user_media (not unique).
        // Let's do a safe Upsert approach: First try to update, if 0 rows affected, insert.
        // Or we can delete first, then insert! That is a 100% robust and clean way to avoid composite null uniqueness issues in Postgres!
        
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var deleteSql = $@"
                DELETE FROM {Schema}.user_history 
                WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
            await connection.ExecuteAsync(deleteSql, history, transaction);

            var insertSql = $@"
                INSERT INTO {Schema}.user_history 
                    (id, user_id, tmdb_id, media_type, season_number, episode_number, progress_seconds, duration_seconds, last_watched_at) 
                VALUES 
                    (@Id, @UserId, @TmdbId, @MediaType, @SeasonNumber, @EpisodeNumber, @ProgressSeconds, @DurationSeconds, @LastWatchedAt)";
            await connection.ExecuteAsync(insertSql, history, transaction);

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteProgressAsync(Guid userId, string tmdbId, string mediaType, int? season = null, int? episode = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            DELETE FROM {Schema}.user_history 
            WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType 
              AND (season_number IS NULL OR season_number = @Season)
              AND (episode_number IS NULL OR episode_number = @Episode)";
        await connection.ExecuteAsync(sql, new
        {
            UserId = userId,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Season = season,
            Episode = episode
        });
    }
}
