using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models;
using Potok.Backend.Core.Models.Database;
using Potok.Backend.Core.Models.Details;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Utils;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Search;

public class LocalSearchService : BaseSearchService, ILocalSearchService
{
    private const string Schema = DbSchema.Name;
    private readonly string _connectionString;
    private readonly ILogger<LocalSearchService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalSearchService(
        IOptions<Config> config,
        TrackerHttpClient httpService,
        ICacheService cacheService,
        string connectionString,
        ILogger<LocalSearchService> logger) : base(config.Value, httpService, cacheService)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<TorrentDetails>> SearchByTitleAsync(
        string? title,
        string? originalTitle,
        int? year = null,
        int? mediaType = null,
        bool exact = false)
    {
        // For now, we prefer SearchByTmdbId. 
        // If only title is provided, we use a basic ILIKE search.
        if (string.IsNullOrWhiteSpace(title)) return [];

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            SELECT * FROM {Schema}.torrents 
            WHERE title ILIKE @Query
            ORDER BY seeders DESC 
            LIMIT 500";

        var rows = await connection.QueryAsync<Torrent>(sql, new { Query = $"%{title}%" });
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<List<TorrentDetails>> SearchByQueryAsync(string? query, int? mediaType = null, bool exact = false)
    {
        return await SearchByTitleAsync(query, null);
    }

    public async Task<List<TorrentDetails>> SearchByTmdbIdAsync(long tmdbId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = $@"
            SELECT * FROM {Schema}.torrents 
            WHERE tmdb_id = @TmdbId
            ORDER BY seeders DESC";

        var rows = await connection.QueryAsync<Torrent>(sql, new { TmdbId = tmdbId });
        return rows.Select(MapToDomain).ToList();
    }

    private TorrentDetails MapToDomain(Torrent db)
    {
        var details = new TorrentDetails
        {
            Id = db.Id,
            TmdbId = db.TmdbId,
            InfoHash = db.InfoHash,
            TrackerName = db.TrackerName,
            Title = db.Title,
            Url = db.Url,
            Size = db.Size,
            Magnet = db.MagnetUri,
            Sid = db.Seeders,
            Pir = db.Leechers,
            CreateTime = db.PublishDate,
            ParsedInfo = !string.IsNullOrEmpty(db.ParsedInfo) 
                ? JsonSerializer.Deserialize<ParsedTorrentInfo>(db.ParsedInfo, JsonOptions) 
                : null
        };

        // Populate legacy fields for ApplyFilters in BaseSearchService
        if (details.ParsedInfo != null)
        {
            details.Relased = details.ParsedInfo.Year;
            details.Quality = StringConvert.ParseQuality(details.ParsedInfo.Resolution);
            details.VideoType = details.ParsedInfo.Quality;
            
            if (details.ParsedInfo.Seasons != null)
                details.Seasons = new HashSet<int>(details.ParsedInfo.Seasons);
            
            if (!string.IsNullOrEmpty(details.ParsedInfo.Audio))
                details.Voices = new HashSet<string> { details.ParsedInfo.Audio };
        }

        return details;
    }
}