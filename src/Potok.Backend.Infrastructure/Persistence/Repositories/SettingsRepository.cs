using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class SettingsRepository : ISettingsRepository
{
    private const string Schema = DbSchema.Name;
    private readonly string _connectionString;

    public SettingsRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureDatabaseAsync()
    {
        // Migrations handle database creation
        await Task.CompletedTask;
    }

    public async Task<string?> GetValueAsync(string key)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT value FROM {Schema}.settings WHERE key = @Key";
        return await connection.QuerySingleOrDefaultAsync<string>(sql, new { Key = key });
    }

    public async Task SetValueAsync(string key, string value)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.settings (key, value) 
            VALUES (@Key, @Value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value";
        await connection.ExecuteAsync(sql, new { Key = key, Value = value });
    }
}
