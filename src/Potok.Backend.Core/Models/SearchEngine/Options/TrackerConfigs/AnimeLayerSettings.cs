using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options.TrackerConfigs;

public class AnimeLayerSettings : BaseTrackerConfig
{
    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}