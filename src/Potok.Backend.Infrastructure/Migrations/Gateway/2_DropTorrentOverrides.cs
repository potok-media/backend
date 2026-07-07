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
        Delete.Table("torrent_overrides").InSchema(DbSchema.GatewayRaw);
    }

    public override void Down()
    {
        Create.Table("torrent_overrides").InSchema(DbSchema.GatewayRaw)
            .WithColumn("hash").AsString().PrimaryKey()
            .WithColumn("season").AsInt32().Nullable()
            .WithColumn("episode_offset").AsInt32().Nullable();
    }
}
