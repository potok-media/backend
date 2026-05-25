using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Migrations.Configurations;

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
}
