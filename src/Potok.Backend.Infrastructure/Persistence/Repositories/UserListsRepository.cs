using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class UserListsRepository : IUserListsRepository
{
    private const string Schema = DbSchema.Gateway;
    private readonly string _connectionString;

    public UserListsRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    // --- Favorites ---

    public async Task<IEnumerable<UserListEntry>> GetFavoritesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT user_id as UserId, tmdb_id as TmdbId, media_type as MediaType, added_at as AddedAt 
            FROM {Schema}.user_favorites 
            WHERE user_id = @UserId 
            ORDER BY added_at DESC";
        return await connection.QueryAsync<UserListEntry>(sql, new { UserId = userId });
    }

    public async Task<bool> IsFavoriteAsync(Guid userId, string tmdbId, string mediaType)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT COUNT(1) FROM {Schema}.user_favorites WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });
        return count > 0;
    }

    public async Task AddFavoriteAsync(UserListEntry entry)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.user_favorites (user_id, tmdb_id, media_type, added_at) 
            VALUES (@UserId, @TmdbId, @MediaType, @AddedAt)
            ON CONFLICT (user_id, tmdb_id, media_type) DO NOTHING";
        await connection.ExecuteAsync(sql, entry);
    }

    public async Task RemoveFavoriteAsync(Guid userId, string tmdbId, string mediaType)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"DELETE FROM {Schema}.user_favorites WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
        await connection.ExecuteAsync(sql, new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });
    }

    // --- Watchlist ---

    public async Task<IEnumerable<UserListEntry>> GetWatchlistAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            SELECT user_id as UserId, tmdb_id as TmdbId, media_type as MediaType, added_at as AddedAt 
            FROM {Schema}.user_watchlist 
            WHERE user_id = @UserId 
            ORDER BY added_at DESC";
        return await connection.QueryAsync<UserListEntry>(sql, new { UserId = userId });
    }

    public async Task<bool> IsInWatchlistAsync(Guid userId, string tmdbId, string mediaType)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT COUNT(1) FROM {Schema}.user_watchlist WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });
        return count > 0;
    }

    public async Task AddToWatchlistAsync(UserListEntry entry)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.user_watchlist (user_id, tmdb_id, media_type, added_at) 
            VALUES (@UserId, @TmdbId, @MediaType, @AddedAt)
            ON CONFLICT (user_id, tmdb_id, media_type) DO NOTHING";
        await connection.ExecuteAsync(sql, entry);
    }

    public async Task RemoveFromWatchlistAsync(Guid userId, string tmdbId, string mediaType)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"DELETE FROM {Schema}.user_watchlist WHERE user_id = @UserId AND tmdb_id = @TmdbId AND media_type = @MediaType";
        await connection.ExecuteAsync(sql, new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });
    }
}
