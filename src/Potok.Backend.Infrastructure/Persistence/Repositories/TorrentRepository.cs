using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using Npgsql;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Tracks;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Persistence.Repositories;

public class TorrentRepository : ITorrentRepository
{
    private const string SearchEngineSchema = DbSchema.SearchEngine;
    private const string GatewaySchema = DbSchema.Gateway;
    private readonly string _connectionString;
    private readonly ITorrentEnricher _torrentEnricher;
    private readonly ILogger<TorrentRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TorrentRepository(
        ITorrentEnricher torrentEnricher,
        ILogger<TorrentRepository> logger,
        string connectionString
        )
    {
        _torrentEnricher = torrentEnricher;
        _logger = logger;
        _connectionString = connectionString;
    }

    public async Task AddOrUpdateAsync(IReadOnlyCollection<TorrentDetails> torrents)
    {
        foreach (var torrent in torrents)
        {
            try
            {
                var enriched = _torrentEnricher.EnrichAndConvert(torrent);
                await UpsertTorrent(enriched);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert torrent {Title}", torrent.Title);
            }
        }
    }

    public async Task AddOrUpdateAsync<T>(IReadOnlyCollection<T> torrents, Func<T, CancellationToken, Task<bool>> predicate, CancellationToken ct) where T : TorrentDetails
    {
        foreach (var torrent in torrents)
        {
            if (ct.IsCancellationRequested) break;
            
            if (predicate != null && !await predicate(torrent, ct))
                continue;

            await AddOrUpdateAsync([torrent]);
        }
    }

    private async Task UpsertTorrent(TorrentDetails src)
    {
        if (string.IsNullOrWhiteSpace(src.Magnet))
            return;

        string infoHash;
        try
        {
            var magnet = MagnetLink.Parse(src.Magnet);
            infoHash = magnet.InfoHashes.V1OrV2.ToHex().ToLower();
        }
        catch
        {
            _logger.LogWarning("Invalid magnet link for {Title}", src.Title);
            return;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            INSERT INTO {SearchEngineSchema}.torrents 
            (info_hash, tmdb_id, tracker_name, title, url, size, magnet_uri, seeders, leechers, publish_date, parsed_info, updated_at)
            VALUES 
            (@InfoHash, @TmdbId, @TrackerName, @Title, @Url, @Size, @MagnetUri, @Seeders, @Leechers, @PublishDate, @ParsedInfo::jsonb, now())
            ON CONFLICT (info_hash) DO UPDATE SET
                tmdb_id = COALESCE(EXCLUDED.tmdb_id, {SearchEngineSchema}.torrents.tmdb_id),
                seeders = EXCLUDED.seeders,
                leechers = EXCLUDED.leechers,
                updated_at = now()";

        await connection.ExecuteAsync(sql, new
        {
            InfoHash = infoHash,
            TmdbId = src.TmdbId,
            TrackerName = src.TrackerName,
            Title = src.Title,
            Url = src.Url,
            Size = (long)src.Size,
            MagnetUri = src.Magnet,
            Seeders = src.Sid,
            Leechers = src.Pir,
            PublishDate = src.CreateTime == default ? DateTime.UtcNow : src.CreateTime,
            ParsedInfo = JsonSerializer.Serialize(src.ParsedInfo, JsonOptions)
        });
    }

    // FFProbe logic is secondary but I'll keep the interface satisfied
    public async Task<List<TorrentDetails>> GetForMediaProbeAsync(int limit, int maxAttempts, IReadOnlyCollection<string>? excludedTypes = null)
    {
        // Currently returning empty as we migrated away from the old probe logic
        return [];
    }

    public async Task UpdateMediaProbeAsync(string url, List<FfStream> ffprobe, HashSet<string>? languages)
    {
        // To be implemented later if needed
    }

    public async Task IncrementMediaProbeAttemptsAsync(string url)
    {
        // To be implemented later if needed
    }
    
    public async Task EnsureDatabaseAsync()
    {
        // Migrations handle database creation
        await Task.CompletedTask;
    }

    public async Task<TorrentOverride?> GetOverrideAsync(string hash)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"SELECT hash, season, episode_offset as EpisodeOffset FROM {GatewaySchema}.torrent_overrides WHERE hash = @Hash";
        return await connection.QuerySingleOrDefaultAsync<TorrentOverride>(sql, new { Hash = hash });
    }

    public async Task SetOverrideAsync(string hash, int? season, int? episodeOffset)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var sql = $@"
            INSERT INTO {GatewaySchema}.torrent_overrides (hash, season, episode_offset) 
            VALUES (@Hash, @Season, @Offset)
            ON CONFLICT (hash) DO UPDATE SET 
                season = EXCLUDED.season, 
                episode_offset = EXCLUDED.episode_offset";
        await connection.ExecuteAsync(sql, new { Hash = hash, Season = season, Offset = episodeOffset });
    }
}