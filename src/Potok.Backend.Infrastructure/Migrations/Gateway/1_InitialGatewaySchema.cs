using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.Gateway;

[Migration(1)]
public class InitialGatewaySchema : Migration
{
    public override void Up()
    {
        var schema = DbSchema.GatewayRaw;
        Execute.Sql($"CREATE SCHEMA IF NOT EXISTS \"{schema}\";");

        // --- Settings Table ---
        Create.Table("settings").InSchema(schema)
            .WithColumn("key").AsString().PrimaryKey()
            .WithColumn("value").AsString().Nullable();

        // --- Torrent Overrides Table ---
        Create.Table("torrent_overrides").InSchema(schema)
            .WithColumn("hash").AsString().PrimaryKey()
            .WithColumn("season").AsInt32().Nullable()
            .WithColumn("episode_offset").AsInt32().Nullable();
    }

    public override void Down()
    {
        var schema = DbSchema.GatewayRaw;
        Delete.Table("torrent_overrides").InSchema(schema);
        Delete.Table("settings").InSchema(schema);
        Execute.Sql($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
    }
}
