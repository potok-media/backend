using System.Text.Json;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
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

    private static Dictionary<string, SeasonOverrideEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, SeasonOverrideEntry>>(json, JsonOptions) ?? new();
    }
}
