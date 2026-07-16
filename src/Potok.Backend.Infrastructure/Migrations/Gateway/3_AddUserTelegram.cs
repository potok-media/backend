using FluentMigrator;
using Potok.Backend.Infrastructure.Migrations.Configurations;

namespace Potok.Backend.Infrastructure.Migrations.Gateway;

// Adds Telegram linking to gateway users: a numeric Telegram id (unique per account) and the
// last-seen Telegram username. Enables login/registration via the Telegram Login Widget and
// linking a Telegram identity to an existing account.
[Migration(3)]
public class AddUserTelegram : Migration
{
    public override void Up()
    {
        var schema = DbSchema.GatewayRaw;

        Alter.Table("users").InSchema(schema)
            .AddColumn("telegram_id").AsInt64().Nullable()
            .AddColumn("telegram_username").AsString(100).Nullable();

        // Unique index; NULL telegram_id values do not collide, so unlinked accounts are unaffected.
        Create.Index("IX_users_telegram_id")
            .OnTable("users").InSchema(schema)
            .OnColumn("telegram_id").Unique();
    }

    public override void Down()
    {
        var schema = DbSchema.GatewayRaw;
        Delete.Index("IX_users_telegram_id").OnTable("users").InSchema(schema);
        Delete.Column("telegram_username").FromTable("users").InSchema(schema);
        Delete.Column("telegram_id").FromTable("users").InSchema(schema);
    }
}
