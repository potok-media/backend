using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities.Gateway;
using Potok.Backend.Core.Interfaces.Gateway;
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
        
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Delete only the exact matching entry (specific episode or movie) before inserting
            var deleteSql = $@"
                DELETE FROM {Schema}.user_history 
                WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType
                  AND (season_number = @SeasonNumber OR (season_number IS NULL AND @SeasonNumber IS NULL))
                  AND (episode_number = @EpisodeNumber OR (episode_number IS NULL AND @EpisodeNumber IS NULL))";
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
            WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
            
        if (season.HasValue)
        {
            sql += " AND season_number = @Season";
        }
        if (episode.HasValue)
        {
            sql += " AND episode_number = @Episode";
        }

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
