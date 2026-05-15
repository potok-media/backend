namespace Potok.Backend.Core.Interfaces;

public interface ISettingsRepository
{
    Task<string?> GetValueAsync(string key);
    Task SetValueAsync(string key, string value);
    Task EnsureDatabaseAsync();
}
