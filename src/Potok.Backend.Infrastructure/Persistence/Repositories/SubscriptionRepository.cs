using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Database;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class SubscriptionRepository : ISubscriptionRepository
{
    private const string Schema = DbSchema.SearchEngine;
    private readonly string _connectionString;

    public SubscriptionRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task AddAsync(Subscription subscription)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            INSERT INTO {Schema}.subscriptions (id, uid, tmdb_id, media, created_at)
            VALUES (@Id, @Uid, @TmdbId, @Media, @CreatedAt)
            ON CONFLICT (uid, tmdb_id, media) DO UPDATE SET created_at = EXCLUDED.created_at";

        await connection.ExecuteAsync(sql, subscription);
    }

    public async Task RemoveAsync(long tmdbId, string uid, string? media = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            DELETE FROM {Schema}.subscriptions
            WHERE uid = @Uid AND tmdb_id = @TmdbId" +
            (media != null ? " AND media = @Media" : "");

        await connection.ExecuteAsync(sql, new { Uid = uid, TmdbId = tmdbId, Media = media });
    }

    public async Task<bool> ExistsAsync(long tmdbId, string uid, string? media = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            SELECT COUNT(1)
            FROM {Schema}.subscriptions
            WHERE uid = @Uid AND tmdb_id = @TmdbId" +
            (media != null ? " AND media = @Media" : "");

        var count = await connection.ExecuteScalarAsync<int>(sql, new { Uid = uid, TmdbId = tmdbId, Media = media });
        return count > 0;
    }
}