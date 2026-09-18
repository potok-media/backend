using Potok.Backend.Core.Models.SearchEngine;
using Potok.Backend.Infrastructure.Persistence.Repositories;

namespace Potok.Backend.CompositionTests;

public class TorrentOverrideSummaryTests
{
    [Fact]
    public void From_NoMaps_ReturnsNull()
    {
        Assert.Null(TorrentOverrideSummary.From(null, null));
        Assert.Null(TorrentOverrideSummary.From(new Dictionary<string, SeasonOverrideEntry>(), new Dictionary<string, FileOverrideEntry>()));
    }

    [Fact]
    public void From_OneSeasonKey_SetsPrimaryFields()
    {
        var seasons = new Dictionary<string, SeasonOverrideEntry>
        {
            ["1"] = new SeasonOverrideEntry(2, -1)
        };

        var summary = TorrentOverrideSummary.From(seasons, null);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.SeasonCount);
        Assert.Equal(0, summary.FileCount);
        Assert.Equal("1", summary.PrimarySource);
        Assert.Equal(2, summary.PrimarySeason);
        Assert.Equal(-1, summary.PrimaryOffset);
    }

    [Fact]
    public void From_SentinelSeasonKey_SetsPrimarySource()
    {
        var seasons = new Dictionary<string, SeasonOverrideEntry>
        {
            ["_"] = new SeasonOverrideEntry(1, 0)
        };

        var summary = TorrentOverrideSummary.From(seasons, new Dictionary<string, FileOverrideEntry>());

        Assert.NotNull(summary);
        Assert.Equal(1, summary.SeasonCount);
        Assert.Equal("_", summary.PrimarySource);
        Assert.Equal(1, summary.PrimarySeason);
        Assert.Equal(0, summary.PrimaryOffset);
    }

    [Fact]
    public void From_TwoSeasonKeys_OmitsPrimary()
    {
        var seasons = new Dictionary<string, SeasonOverrideEntry>
        {
            ["1"] = new SeasonOverrideEntry(1, 0),
            ["2"] = new SeasonOverrideEntry(2, 0)
        };

        var summary = TorrentOverrideSummary.From(seasons, null);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.SeasonCount);
        Assert.Equal(0, summary.FileCount);
        Assert.Null(summary.PrimarySource);
        Assert.Null(summary.PrimarySeason);
        Assert.Null(summary.PrimaryOffset);
    }

    [Fact]
    public void From_FilesOnly_HasFileCount()
    {
        var files = new Dictionary<string, FileOverrideEntry>
        {
            ["file-a"] = new FileOverrideEntry(1, 1, "anchor"),
            ["file-b"] = new FileOverrideEntry(1, 2, "pin")
        };

        var summary = TorrentOverrideSummary.From(null, files);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.SeasonCount);
        Assert.Equal(2, summary.FileCount);
        Assert.Null(summary.PrimarySource);
        Assert.Null(summary.PrimarySeason);
        Assert.Null(summary.PrimaryOffset);
    }

    [Fact]
    public async Task GetSummariesAsync_EmptyHashes_ReturnsEmptyWithoutQuery()
    {
        var repo = new SeasonOverrideRepository("Host=invalid;Database=none");

        var empty = await repo.GetSummariesAsync(Array.Empty<string>());
        var whitespace = await repo.GetSummariesAsync(["", "  "]);

        Assert.Empty(empty);
        Assert.Empty(whitespace);
    }

    [Fact]
    public async Task FakeRepository_AttachesOverrideOnlyWhenMapsExist()
    {
        var hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var repo = new FakeSeasonOverrideRepository();
        repo.Rows[hash] = (
            new Dictionary<string, SeasonOverrideEntry> { ["1"] = new SeasonOverrideEntry(3, 2) },
            new Dictionary<string, FileOverrideEntry>());

        var summaries = await repo.GetSummariesAsync([hash, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]);

        Assert.True(summaries.ContainsKey(hash));
        Assert.False(summaries.ContainsKey("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var summary = summaries[hash];
        Assert.Equal(1, summary.SeasonCount);
        Assert.Equal("1", summary.PrimarySource);
        Assert.Equal(3, summary.PrimarySeason);
        Assert.Equal(2, summary.PrimaryOffset);
    }

    [Fact]
    public async Task FakeRepository_EmptyMaps_AreOmitted()
    {
        var hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var repo = new FakeSeasonOverrideRepository();
        repo.Rows[hash] = (new Dictionary<string, SeasonOverrideEntry>(), new Dictionary<string, FileOverrideEntry>());

        var summaries = await repo.GetSummariesAsync([hash]);

        Assert.Empty(summaries);
    }

    private sealed class FakeSeasonOverrideRepository : ISeasonOverrideRepository
    {
        public Dictionary<string, (Dictionary<string, SeasonOverrideEntry> Seasons, Dictionary<string, FileOverrideEntry> Files)> Rows { get; } = new();

        public Task<Dictionary<string, SeasonOverrideEntry>> GetAsync(string hash) =>
            Task.FromResult(Rows.GetValueOrDefault(hash).Seasons ?? new());

        public Task<Dictionary<string, SeasonOverrideEntry>> UpsertSeasonAsync(string hash, string sourceKey, SeasonOverrideEntry entry) =>
            throw new NotImplementedException();

        public Task<Dictionary<string, SeasonOverrideEntry>> RemoveSeasonAsync(string hash, string sourceKey) =>
            throw new NotImplementedException();

        public Task ReplaceAsync(string hash, Dictionary<string, SeasonOverrideEntry> map) =>
            throw new NotImplementedException();

        public Task<Dictionary<string, FileOverrideEntry>> GetFileMapAsync(string hash) =>
            Task.FromResult(Rows.GetValueOrDefault(hash).Files ?? new());

        public Task<Dictionary<string, FileOverrideEntry>> UpsertFileAsync(string hash, string fileId, FileOverrideEntry entry) =>
            throw new NotImplementedException();

        public Task<Dictionary<string, FileOverrideEntry>> RemoveFileAsync(string hash, string fileId) =>
            throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, TorrentOverrideSummary>> GetSummariesAsync(IReadOnlyCollection<string> hashes)
        {
            var result = new Dictionary<string, TorrentOverrideSummary>();
            foreach (var hash in hashes)
            {
                if (!Rows.TryGetValue(hash.ToLower(), out var maps)) continue;
                var summary = TorrentOverrideSummary.From(maps.Seasons, maps.Files);
                if (summary is null) continue;
                result[hash.ToLower()] = summary;
            }
            return Task.FromResult<IReadOnlyDictionary<string, TorrentOverrideSummary>>(result);
        }
    }
}
