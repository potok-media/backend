using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class SettingsRepository : ISettingsRepository
{
    private readonly string _connectionString;

    public SettingsRepository(IOptions<GatewayOptions> options)
    {
        _connectionString = options.Value.SettingsDbConnection;
    }

    public async Task EnsureDatabaseAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)";
        await connection.ExecuteAsync(sql);
    }

    public async Task<string?> GetValueAsync(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "SELECT value FROM settings WHERE key = @Key";
        return await connection.QuerySingleOrDefaultAsync<string>(sql, new { Key = key });
    }

    public async Task SetValueAsync(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = "INSERT OR REPLACE INTO settings (key, value) VALUES (@Key, @Value)";
        await connection.ExecuteAsync(sql, new { Key = key, Value = value });
    }
}
