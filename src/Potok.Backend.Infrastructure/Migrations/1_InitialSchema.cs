using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.SearchEngine.Infrastructure.Migrations;

[Migration(1)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        var schema = DbSchema.Name;
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        Execute.Sql($"CREATE SCHEMA IF NOT EXISTS {schema};");

        // --- Torrents Table ---
        Create.Table("torrents").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey().WithDefaultValue(SystemMethods.NewGuid)
            .WithColumn("info_hash").AsString().NotNullable().Unique()
            .WithColumn("tmdb_id").AsInt64().Nullable().Indexed()
            .WithColumn("tracker_name").AsString().NotNullable()
            .WithColumn("title").AsString().NotNullable()
            .WithColumn("url").AsString().NotNullable()
            .WithColumn("size").AsInt64().NotNullable()
            .WithColumn("magnet_uri").AsString().NotNullable()
            .WithColumn("seeders").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("leechers").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("publish_date").AsDateTimeOffset().NotNullable()
            .WithColumn("parsed_info").AsCustom("jsonb").Nullable()
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_torrents_tmdb_id").OnTable("torrents").InSchema(schema).OnColumn("tmdb_id");
        Create.Index("ix_torrents_info_hash").OnTable("torrents").InSchema(schema).OnColumn("info_hash");
        Create.Index("ix_torrents_seeders").OnTable("torrents").InSchema(schema).OnColumn("seeders").Descending();
        Execute.Sql($"CREATE INDEX IF NOT EXISTS ix_torrents_title_trgm ON {schema}.torrents USING gin (title gin_trgm_ops);");

        // --- Queries Table (for background refresh) ---
        Create.Table("queries").InSchema(schema)
            .WithColumn("tmdb_id").AsInt64().PrimaryKey()
            .WithColumn("query").AsString().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_seen").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(1)
            .WithColumn("last_refresh_time").AsDateTimeOffset().Nullable();

        // --- Subscriptions Table ---
        Create.Table("subscriptions").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey().WithDefaultValue(SystemMethods.NewGuid)
            .WithColumn("tmdb_id").AsInt64().NotNullable()
            .WithColumn("uid").AsString().NotNullable()
            .WithColumn("media").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("FK_subscriptions_queries")
            .FromTable("subscriptions").InSchema(schema).ForeignColumn("tmdb_id")
            .ToTable("queries").InSchema(schema).PrimaryColumn("tmdb_id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("IX_subscriptions_uid_tmdb_id_media").OnTable("subscriptions").InSchema(schema)
            .OnColumn("uid").Ascending()
            .OnColumn("tmdb_id").Ascending()
            .OnColumn("media").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        var schema = DbSchema.Name;
        Delete.Table("subscriptions").InSchema(schema);
        Delete.Table("queries").InSchema(schema);
        Delete.Table("torrents").InSchema(schema);
        Execute.Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE;");
    }
}
