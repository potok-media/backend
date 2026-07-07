using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

// Per-season torrent overrides, stored in the SearchEngine schema (moved out of the gateway). Keyed by torrent
// infohash; the value is a source-season → {targetSeason, offset} map (sentinel key "_" = files with no season).
public interface ISeasonOverrideRepository
{
    Task<Dictionary<string, SeasonOverrideEntry>> GetAsync(string hash);
    Task<Dictionary<string, SeasonOverrideEntry>> UpsertSeasonAsync(string hash, string sourceKey, SeasonOverrideEntry entry);
    Task<Dictionary<string, SeasonOverrideEntry>> RemoveSeasonAsync(string hash, string sourceKey);
    Task ReplaceAsync(string hash, Dictionary<string, SeasonOverrideEntry> map);
}
