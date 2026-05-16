using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.SearchEngine.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class InfuseRepository : IInfuseRepository
{
    private const string Schema = DbSchema.Name;
    private readonly string _connectionString;

    public InfuseRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureDatabaseAsync()
    {
        // Migrations handle database creation
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<InfuseLibraryItem>> GetAllAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT id, tmdb_id AS TmdbId, media_type AS MediaType, title, poster, torrent_title AS TorrentTitle, torrent_hash AS TorrentHash, magnet_uri AS MagnetUri, link, status, created_at AS CreatedAt, updated_at AS UpdatedAt FROM {Schema}.infuse_items ORDER BY created_at DESC";
        return await connection.QueryAsync<InfuseLibraryItem>(sql);
    }

    public async Task<InfuseLibraryItem?> GetByTmdbIdAsync(long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"SELECT id, tmdb_id AS TmdbId, media_type AS MediaType, title, poster, torrent_title AS TorrentTitle, torrent_hash AS TorrentHash, magnet_uri AS MagnetUri, link, status, created_at AS CreatedAt, updated_at AS UpdatedAt FROM {Schema}.infuse_items WHERE tmdb_id = @TmdbId LIMIT 1";
        return await connection.QuerySingleOrDefaultAsync<InfuseLibraryItem>(sql, new { TmdbId = tmdbId });
    }

    public async Task SaveAsync(InfuseLibraryItem item)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {Schema}.infuse_items 
            (id, tmdb_id, media_type, title, poster, torrent_title, torrent_hash, magnet_uri, link, status, created_at, updated_at) 
            VALUES 
            (@Id, @TmdbId, @MediaType, @Title, @Poster, @TorrentTitle, @TorrentHash, @MagnetUri, @Link, @Status, @CreatedAt, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE SET
                tmdb_id = EXCLUDED.tmdb_id,
                media_type = EXCLUDED.media_type,
                title = EXCLUDED.title,
                poster = EXCLUDED.poster,
                torrent_title = EXCLUDED.torrent_title,
                torrent_hash = EXCLUDED.torrent_hash,
                magnet_uri = EXCLUDED.magnet_uri,
                link = EXCLUDED.link,
                status = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at";
        
        await connection.ExecuteAsync(sql, new
        {
            item.Id,
            item.TmdbId,
            item.MediaType,
            item.Title,
            item.Poster,
            item.TorrentTitle,
            item.TorrentHash,
            item.MagnetUri,
            item.Link,
            Status = (int)item.Status,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        });
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"DELETE FROM {Schema}.infuse_items WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = id });
    }

    public async Task UpdateStatusAsync(Guid id, InfuseItemStatus status)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $"UPDATE {Schema}.infuse_items SET status = @Status, updated_at = now() WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = id, Status = (int)status });
    }
}
