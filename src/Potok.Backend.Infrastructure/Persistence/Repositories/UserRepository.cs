using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Migrations.Configurations;
using Potok.Backend.Infrastructure.Security;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class UserRepository : IUserRepository
{
    private const string Schema = DbSchema.Gateway;
    private readonly string _connectionString;

    public UserRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<User?> GetByIdAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT id, username, password_hash as PasswordHash, sync_strategy as SyncStrategy, created_at as CreatedAt FROM {Schema}.users WHERE id = @Id";
        return await connection.QuerySingleOrDefaultAsync<User>(sql, new { Id = id });
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT id, username, password_hash as PasswordHash, sync_strategy as SyncStrategy, created_at as CreatedAt FROM {Schema}.users WHERE LOWER(username) = LOWER(@Username)";
        return await connection.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });
    }

    public async Task CreateAsync(User user)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.users (id, username, password_hash, sync_strategy, created_at)
            VALUES (@Id, @Username, @PasswordHash, @SyncStrategy, @CreatedAt)";
        await connection.ExecuteAsync(sql, user);
    }

    public async Task UpdateSyncStrategyAsync(Guid userId, string strategy)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"UPDATE {Schema}.users SET sync_strategy = @Strategy WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = userId, Strategy = strategy });
    }

    public async Task<UserTraktToken?> GetTraktTokenAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT user_id as UserId, access_token as AccessToken, refresh_token as RefreshToken, expires_at as ExpiresAt FROM {Schema}.user_trakt_tokens WHERE user_id = @UserId";
        var token = await connection.QuerySingleOrDefaultAsync<UserTraktToken>(sql, new { UserId = userId });

        if (token != null)
        {
            token.AccessToken = TokenEncryptor.Decrypt(token.AccessToken);
            if (!string.IsNullOrEmpty(token.RefreshToken))
            {
                token.RefreshToken = TokenEncryptor.Decrypt(token.RefreshToken);
            }
        }

        return token;
    }

    public async Task SaveTraktTokenAsync(UserTraktToken token)
    {
        var encryptedToken = new UserTraktToken
        {
            UserId = token.UserId,
            AccessToken = TokenEncryptor.Encrypt(token.AccessToken),
            RefreshToken = !string.IsNullOrEmpty(token.RefreshToken) ? TokenEncryptor.Encrypt(token.RefreshToken) : null,
            ExpiresAt = token.ExpiresAt
        };

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.user_trakt_tokens (user_id, access_token, refresh_token, expires_at)
            VALUES (@UserId, @AccessToken, @RefreshToken, @ExpiresAt)
            ON CONFLICT (user_id) DO UPDATE SET
                access_token = EXCLUDED.access_token,
                refresh_token = EXCLUDED.refresh_token,
                expires_at = EXCLUDED.expires_at";
        await connection.ExecuteAsync(sql, encryptedToken);
    }

    public async Task DeleteTraktTokenAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"DELETE FROM {Schema}.user_trakt_tokens WHERE user_id = @UserId";
        await connection.ExecuteAsync(sql, new { UserId = userId });
    }
}
