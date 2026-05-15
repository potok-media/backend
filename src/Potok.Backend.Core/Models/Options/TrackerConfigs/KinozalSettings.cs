using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.Options.TrackerConfigs;

public class KinozalSettings : BaseTrackerConfig
{
    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}