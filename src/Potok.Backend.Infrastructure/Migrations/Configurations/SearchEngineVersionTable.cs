using FluentMigrator.Runner.VersionTableInfo;

namespace Potok.Backend.Infrastructure.Migrations.Configurations;

public class SearchEngineVersionTable : IVersionTableMetaData
{
    public string SchemaName => DbSchema.Name;
    public string TableName => "VersionInfo";
    public string ColumnName => "Version";
    public string UniqueIndexName => string.Empty;
    public string AppliedOnColumnName => "AppliedOn";
    public bool CreateWithPrimaryKey => true;
    public string DescriptionColumnName => "FluentMigrator version table";
    public bool OwnsSchema => true;
    public object? ApplicationContext { get; set; }
}