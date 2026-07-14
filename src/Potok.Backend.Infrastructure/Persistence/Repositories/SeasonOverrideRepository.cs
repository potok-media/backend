using System.Text.Json;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class SeasonOverrideRepository : ISeasonOverrideRepository
{
    private const string Schema = DbSchema.SearchEngine;
    private readonly string _connectionString;

    // CamelCase so the JSONB matches what the plugins read (season/offset lowercase) — mirrors TorrentRepository.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SeasonOverrideRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<Dictionary<string, SeasonOverrideEntry>> GetAsync(string hash)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"SELECT season_map FROM {Schema}.torrent_overrides WHERE hash = @Hash";
        var json = await connection.QuerySingleOrDefaultAsync<string>(sql, new { Hash = cleanHash });
        return Deserialize(json);
    }

    public async Task<Dictionary<string, SeasonOverrideEntry>> UpsertSeasonAsync(string hash, string sourceKey, SeasonOverrideEntry entry)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // Patch ONE source-season key. jsonb_set needs a bound text[] path (ARRAY[@Key]) — a @Key inside a '{...}'
        // string literal would NOT bind. On a fresh row, jsonb_build_object seeds the single-entry map.
        var sql = $@"
            INSERT INTO {Schema}.torrent_overrides (hash, season_map, updated_at)
            VALUES (@Hash, jsonb_build_object(@Key, @Entry::jsonb), now())
            ON CONFLICT (hash) DO UPDATE SET
                season_map = jsonb_set(COALESCE({Schema}.torrent_overrides.season_map, '{{}}'::jsonb), ARRAY[@Key], @Entry::jsonb),
                updated_at = now()
            RETURNING season_map";
        var json = await connection.ExecuteScalarAsync<string>(sql, new
        {
            Hash = cleanHash,
            Key = sourceKey,
            Entry = JsonSerializer.Serialize(entry, JsonOptions)
        });
        return Deserialize(json);
    }

    public async Task<Dictionary<string, SeasonOverrideEntry>> RemoveSeasonAsync(string hash, string sourceKey)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // jsonb `-` text removes one top-level key. Returns the updated map (empty if the row is gone/absent).
        var sql = $@"
            UPDATE {Schema}.torrent_overrides
            SET season_map = season_map - @Key, updated_at = now()
            WHERE hash = @Hash
            RETURNING season_map";
        var json = await connection.ExecuteScalarAsync<string>(sql, new { Hash = cleanHash, Key = sourceKey });
        return Deserialize(json);
    }

    public async Task ReplaceAsync(string hash, Dictionary<string, SeasonOverrideEntry> map)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.torrent_overrides (hash, season_map, updated_at)
            VALUES (@Hash, @Map::jsonb, now())
            ON CONFLICT (hash) DO UPDATE SET season_map = @Map::jsonb, updated_at = now()";
        await connection.ExecuteAsync(sql, new { Hash = cleanHash, Map = JsonSerializer.Serialize(map, JsonOptions) });
    }

    // ---- Phase 2: per-file overrides (file_map jsonb on the same row) ----

    public async Task<Dictionary<string, FileOverrideEntry>> GetFileMapAsync(string hash)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"SELECT file_map FROM {Schema}.torrent_overrides WHERE hash = @Hash";
        var json = await connection.QuerySingleOrDefaultAsync<string>(sql, new { Hash = cleanHash });
        return DeserializeFiles(json);
    }

    public async Task<Dictionary<string, FileOverrideEntry>> UpsertFileAsync(string hash, string fileId, FileOverrideEntry entry)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // Patch ONE file id under file_map (mirrors UpsertSeasonAsync). On a fresh row season_map falls back to
        // its column default '{}'. ARRAY[@Key] is a bound text[] path so an arbitrary file id binds safely.
        var sql = $@"
            INSERT INTO {Schema}.torrent_overrides (hash, file_map, updated_at)
            VALUES (@Hash, jsonb_build_object(@Key, @Entry::jsonb), now())
            ON CONFLICT (hash) DO UPDATE SET
                file_map = jsonb_set(COALESCE({Schema}.torrent_overrides.file_map, '{{}}'::jsonb), ARRAY[@Key], @Entry::jsonb),
                updated_at = now()
            RETURNING file_map";
        var json = await connection.ExecuteScalarAsync<string>(sql, new
        {
            Hash = cleanHash,
            Key = fileId,
            Entry = JsonSerializer.Serialize(entry, JsonOptions)
        });
        return DeserializeFiles(json);
    }

    public async Task<Dictionary<string, FileOverrideEntry>> RemoveFileAsync(string hash, string fileId)
    {
        var cleanHash = hash?.ToLower() ?? string.Empty;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            UPDATE {Schema}.torrent_overrides
            SET file_map = file_map - @Key, updated_at = now()
            WHERE hash = @Hash
            RETURNING file_map";
        var json = await connection.ExecuteScalarAsync<string>(sql, new { Hash = cleanHash, Key = fileId });
        return DeserializeFiles(json);
    }

    private static Dictionary<string, SeasonOverrideEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, SeasonOverrideEntry>>(json, JsonOptions) ?? new();
    }

    private static Dictionary<string, FileOverrideEntry> DeserializeFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, FileOverrideEntry>>(json, JsonOptions) ?? new();
    }
}
