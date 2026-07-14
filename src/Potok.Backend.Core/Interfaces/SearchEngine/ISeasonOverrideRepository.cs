using Potok.Backend.Core.Models.SearchEngine;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

// Per-season torrent overrides, stored in the SearchEngine schema (moved out of the gateway). Keyed by torrent
// infohash; the value is a source-season → {targetSeason, offset} map (sentinel key "_" = files with no season).
public interface ISeasonOverrideRepository
{
    Task<Dictionary<string, SeasonOverrideEntry>> GetAsync(string hash);
    Task<Dictionary<string, SeasonOverrideEntry>> UpsertSeasonAsync(string hash, string sourceKey, SeasonOverrideEntry entry);
    Task<Dictionary<string, SeasonOverrideEntry>> RemoveSeasonAsync(string hash, string sourceKey);
    Task ReplaceAsync(string hash, Dictionary<string, SeasonOverrideEntry> map);

    // Phase 2: per-file overrides (file_map jsonb), same row/hash. Return the whole file map after each write.
    Task<Dictionary<string, FileOverrideEntry>> GetFileMapAsync(string hash);
    Task<Dictionary<string, FileOverrideEntry>> UpsertFileAsync(string hash, string fileId, FileOverrideEntry entry);
    Task<Dictionary<string, FileOverrideEntry>> RemoveFileAsync(string hash, string fileId);
}
