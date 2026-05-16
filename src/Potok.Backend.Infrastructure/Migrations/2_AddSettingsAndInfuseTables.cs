using FluentMigrator;
using Potok.Backend.Infrastructure.SearchEngine.Migrations.Configurations;

namespace Potok.SearchEngine.Infrastructure.Migrations;

[Migration(2)]
public class AddSettingsAndInfuseTables : Migration
{
    public override void Up()
    {
        var schema = DbSchema.Name;

        // --- Settings Table (Migrated from SQLite) ---
        Create.Table("settings").InSchema(schema)
            .WithColumn("key").AsString().PrimaryKey()
            .WithColumn("value").AsString().Nullable();

        // --- Torrent Overrides Table (Migrated from SQLite) ---
        Create.Table("torrent_overrides").InSchema(schema)
            .WithColumn("hash").AsString().PrimaryKey()
            .WithColumn("season").AsInt32().Nullable()
            .WithColumn("episode_offset").AsInt32().Nullable();

        // --- Infuse Library Items Table ---
        Create.Table("infuse_items").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey().WithDefaultValue(SystemMethods.NewGuid)
            .WithColumn("tmdb_id").AsInt64().NotNullable()
            .WithColumn("media_type").AsString().NotNullable()
            .WithColumn("title").AsString().NotNullable()
            .WithColumn("poster").AsString().Nullable()
            .WithColumn("torrent_title").AsString().Nullable()
            .WithColumn("torrent_hash").AsString().NotNullable()
            .WithColumn("magnet_uri").AsString().NotNullable()
            .WithColumn("link").AsString().Nullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_infuse_items_tmdb_id").OnTable("infuse_items").InSchema(schema).OnColumn("tmdb_id");
    }

    public override void Down()
    {
        var schema = DbSchema.Name;
        Delete.Table("infuse_items").InSchema(schema);
        Delete.Table("torrent_overrides").InSchema(schema);
        Delete.Table("settings").InSchema(schema);
    }
}
