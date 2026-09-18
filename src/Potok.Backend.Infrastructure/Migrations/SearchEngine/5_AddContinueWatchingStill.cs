using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.SearchEngine;

[Migration(5)]
public class AddContinueWatchingStill : Migration
{
    public override void Up()
    {
        Alter.Table("continue_watching").InSchema(DbSchema.SearchEngineRaw)
            .AddColumn("still_src").AsString().Nullable();
    }

    public override void Down()
    {
        Delete.Column("still_src").FromTable("continue_watching").InSchema(DbSchema.SearchEngineRaw);
    }
}
