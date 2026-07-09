using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.Gateway;

// torrent_overrides moved to the SearchEngine schema (per-season model). Drop the old gateway table on existing
// DBs (editing migration 1 only affects fresh DBs). Safe: nothing in the gateway reads it anymore.
[Migration(2)]
public class DropTorrentOverrides : Migration
{
    public override void Up()
    {
        Delete.Table("torrent_overrides").IfExists().InSchema(DbSchema.GatewayRaw);
    }

    public override void Down()
    {
        var schema = DbSchema.GatewayRaw;
        Execute.Sql($"""
            CREATE TABLE IF NOT EXISTS "{schema}".torrent_overrides (
                hash text NOT NULL PRIMARY KEY,
                season integer NULL,
                episode_offset integer NULL
            );
            """);
    }
}
