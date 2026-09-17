using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.SearchEngine;

// Phase 2 of torrent overrides: per-FILE overrides alongside the coarse per-season season_map. file_map jsonb
// holds fileId → {season, episode, mode} where mode is "anchor" (start of a renumbered run) or "pin" (a single
// file fixed in place, e.g. a special, that does not shift its neighbours). Additive — season_map is untouched.
[Migration(3)]
public class AddTorrentFileOverrides : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;
        Alter.Table("torrent_overrides").InSchema(schema)
            .AddColumn("file_map").AsCustom("jsonb").NotNullable().WithDefaultValue("{}");
    }

    public override void Down()
    {
        Delete.Column("file_map").FromTable("torrent_overrides").InSchema(DbSchema.SearchEngineRaw);
    }
}
