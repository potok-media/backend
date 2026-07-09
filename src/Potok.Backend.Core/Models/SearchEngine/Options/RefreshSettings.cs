using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

public class RefreshSettings
{
    [ConfigurationKeyName("enable")] public bool Enable { get; set; } = false;

    [ConfigurationKeyName("timeout")] public int TimeOut { get; set; } = 60;

    [ConfigurationKeyName("older-than-min")]
    public long OlderThanMin { get; set; } = 120;

    [ConfigurationKeyName("limit")] public int Limit { get; set; } = 100;
}