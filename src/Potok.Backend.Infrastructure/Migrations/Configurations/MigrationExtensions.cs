using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Infrastructure.Migrations.Gateway;

namespace Potok.Backend.Infrastructure.Migrations.Configurations;

public static class MigrationExtensions
{
    public static IServiceCollection AddGatewayMigrations(this IServiceCollection services, string connectionString)
    {
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialGatewaySchema).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .Configure<TypeFilterOptions>(opt =>
            {
                opt.Namespace = "Potok.Backend.Infrastructure.Migrations.Gateway";
                opt.NestedNamespaces = true;
            })
            .AddScoped<IVersionTableMetaData, GatewayVersionTable>();

        return services;
    }

    public static void RunGatewayMigrations(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}