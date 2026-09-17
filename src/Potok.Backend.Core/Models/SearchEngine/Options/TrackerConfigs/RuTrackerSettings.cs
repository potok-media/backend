using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options.TrackerConfigs;

public class RuTrackerSettings : BaseTrackerConfig
{
    /// <summary>
    ///     Обновление популярных раздач по категориям
    /// </summary>
    [ConfigurationKeyName("popular")]
    public Popular Popular { get; set; } = new();

    /// <summary>
    ///     Данные для авторизации на трекере.
    /// </summary>
    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}

public class Popular
{
    [ConfigurationKeyName("enable")] public bool Enable { get; set; } = false;

    [ConfigurationKeyName("timeout")] public int TimeOut { get; set; }

    [ConfigurationKeyName("max-pages")] public int MaxPages { get; set; }

    [ConfigurationKeyName("categories")] public IReadOnlyCollection<int> Categories { get; set; } = [];
}