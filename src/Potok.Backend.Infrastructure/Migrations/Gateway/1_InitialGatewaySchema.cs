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

        // torrent_overrides moved to the SearchEngine schema (see SearchEngine migration 2 + gateway migration 2).

        Create.Table("users").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("username").AsString(100).Unique().NotNullable()
            .WithColumn("password_hash").AsString(500).Nullable()
            .WithColumn("sync_strategy").AsString(50).NotNullable().WithDefaultValue("none")
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.Table("user_trakt_tokens").InSchema(schema)
            .WithColumn("user_id").AsGuid().PrimaryKey()
                .ForeignKey("FK_user_trakt_tokens_users", schema, "users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("access_token").AsString(500).NotNullable()
            .WithColumn("refresh_token").AsString(500).Nullable()
            .WithColumn("expires_at").AsDateTime().Nullable();

        Create.Table("user_history").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("FK_user_history_users", schema, "users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("tmdb_id").AsString(50).NotNullable()
            .WithColumn("media_type").AsString(50).NotNullable()
            .WithColumn("season_number").AsInt32().Nullable()
            .WithColumn("episode_number").AsInt32().Nullable()
            .WithColumn("progress_seconds").AsInt64().NotNullable()
            .WithColumn("duration_seconds").AsInt64().NotNullable()
            .WithColumn("last_watched_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.Index("IX_user_history_user_media")
            .OnTable("user_history").InSchema(schema)
            .OnColumn("user_id").Ascending()
            .OnColumn("tmdb_id").Ascending()
            .OnColumn("media_type").Ascending();

        Create.Table("user_favorites").InSchema(schema)
            .WithColumn("user_id").AsGuid().PrimaryKey()
                .ForeignKey("FK_user_favorites_users", schema, "users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("tmdb_id").AsString(50).PrimaryKey()
            .WithColumn("media_type").AsString(50).PrimaryKey()
            .WithColumn("added_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.Table("user_watchlist").InSchema(schema)
            .WithColumn("user_id").AsGuid().PrimaryKey()
                .ForeignKey("FK_user_watchlist_users", schema, "users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("tmdb_id").AsString(50).PrimaryKey()
            .WithColumn("media_type").AsString(50).PrimaryKey()
            .WithColumn("added_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime);
    }

    public override void Down()
    {
        var schema = DbSchema.GatewayRaw;
        Delete.Table("user_watchlist").InSchema(schema);
        Delete.Table("user_favorites").InSchema(schema);
        Delete.Table("user_history").InSchema(schema);
        Delete.Table("user_trakt_tokens").InSchema(schema);
        Delete.Table("users").InSchema(schema);
        Execute.Sql($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
    }
}
