using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class QueriesRepository : IQueriesRepository
{
    private const string Schema = DbSchema.SearchEngine;
    private readonly string _connectionString;

    public QueriesRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyCollection<string>> GetSearchQueriesAsync(int limit)
    {
        if (limit <= 0)
            return [];

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            SELECT query
            FROM {Schema}.queries
            ORDER BY last_seen DESC, hits DESC
            LIMIT @Limit";

        var rows = await connection.QueryAsync<string>(sql, new { Limit = limit });

        return rows
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<StaleQuery>> GetStaleSearchQueriesAsync(TimeSpan olderThan, int limit)
    {
        if (limit <= 0)
            return [];

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var cutoff = DateTimeOffset.UtcNow - olderThan;

        var sql = $@"
            SELECT query, tmdb_id AS ""TmdbId""
            FROM {Schema}.queries
            WHERE last_refresh_time IS NULL OR last_refresh_time < @Cutoff
            ORDER BY last_seen DESC, hits DESC
            LIMIT @Limit";

        var rows = await connection.QueryAsync<StaleQuery>(sql, new { Cutoff = cutoff, Limit = limit });

        return rows.ToArray();
    }

    public async Task TrackSearchQueryAsync(long tmdbId, string query)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            INSERT INTO {Schema}.queries (tmdb_id, query, created_at, last_seen, hits)
            VALUES (@TmdbId, @Query, now(), now(), 1)
            ON CONFLICT (tmdb_id)
            DO UPDATE SET
                last_seen = now(),
                hits = {Schema}.queries.hits + 1,
                query = @Query";

        await connection.ExecuteAsync(sql, new { TmdbId = tmdbId, Query = query });
    }

    public async Task UpdateLastRefreshTimeAsync(long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            UPDATE {Schema}.queries
            SET last_refresh_time = now()
            WHERE tmdb_id = @TmdbId";

        await connection.ExecuteAsync(sql, new { TmdbId = tmdbId });
    }

    public async Task RemoveQueryIfNoSubscriptionsAsync(long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            DELETE FROM {Schema}.queries
            WHERE tmdb_id = @TmdbId
              AND NOT EXISTS (
                SELECT 1 FROM {Schema}.subscriptions
                WHERE tmdb_id = @TmdbId
              )";

        await connection.ExecuteAsync(sql, new { TmdbId = tmdbId });
    }

    public async Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            SELECT s.tmdb_id AS ""TmdbId"", s.media AS ""Media"", q.last_refresh_time AS ""LastRefreshTime""
            FROM {Schema}.subscriptions s
            LEFT JOIN {Schema}.queries q ON s.tmdb_id = q.tmdb_id
            WHERE s.uid = @Uid";

        var rows = await connection.QueryAsync<UserSubscriptionItem>(sql, new { Uid = uid });

        return rows.ToArray();
    }
}