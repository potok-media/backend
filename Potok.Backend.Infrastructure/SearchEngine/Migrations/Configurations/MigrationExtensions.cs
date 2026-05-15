using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Potok.SearchEngine.Infrastructure.Migrations;

namespace Potok.Backend.Infrastructure.SearchEngine.Migrations.Configurations;

public static class MigrationExtensions
{
    public static IServiceCollection AddSearchEngineMigrations(this IServiceCollection services, string connectionString)
    {
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialSchema).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .AddScoped<IVersionTableMetaData, SearchEngineVersionTable>();

        return services;
    }

    public static void RunSearchEngineMigrations(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}