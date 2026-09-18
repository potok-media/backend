using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.SearchEngine;

// Continue-watching cursors for the torrents plugin. One row per title on this SearchEngine instance
// (same isolation model as torrent_overrides — no Gateway user id). Stream jsonb is the opaque
// torrent payload the plugin needs to re-add the release in TorrentGo.
[Migration(4)]
public class AddContinueWatching : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;
        Create.Table("continue_watching").InSchema(schema)
            .WithColumn("media_type").AsString().NotNullable().PrimaryKey()
            .WithColumn("tmdb_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("title").AsString().NotNullable()
            .WithColumn("poster_src").AsString().Nullable()
            .WithColumn("backdrop_src").AsString().Nullable()
            .WithColumn("file_index").AsString().NotNullable()
            .WithColumn("season").AsInt32().Nullable()
            .WithColumn("episode").AsInt32().Nullable()
            .WithColumn("progress_seconds").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("duration_seconds").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("audio_name").AsString().Nullable()
            .WithColumn("info_hash").AsString().Nullable()
            .WithColumn("stream").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_continue_watching_updated_at")
            .OnTable("continue_watching").InSchema(schema)
            .OnColumn("updated_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("continue_watching").IfExists().InSchema(DbSchema.SearchEngineRaw);
    }
}
