using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.SearchEngine;

// Per-season torrent overrides moved out of the gateway into the SearchEngine schema. One row per infohash; the
// season_map jsonb holds source-season → {season, offset} (sentinel key "_" for files with no parseable season).
[Migration(2)]
public class AddTorrentOverrides : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;
        Create.Table("torrent_overrides").InSchema(schema)
            .WithColumn("hash").AsString().PrimaryKey()
            .WithColumn("season_map").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);
    }

    public override void Down()
    {
        Delete.Table("torrent_overrides").IfExists().InSchema(DbSchema.SearchEngineRaw);
    }
}
