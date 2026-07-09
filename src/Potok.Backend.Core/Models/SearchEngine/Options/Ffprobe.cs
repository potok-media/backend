using Microsoft.Extensions.Configuration;

namespace Potok.Backend.Core.Models.SearchEngine.Options;

public class Ffprobe
{
    [ConfigurationKeyName("enable")] public bool Enable { get; set; } = false;

    [ConfigurationKeyName("timeout")] public long TimeOut { get; set; }

    [ConfigurationKeyName("tsuri")] public string? TsUri { get; set; }

    [ConfigurationKeyName("batch-size")] public int BatchSize { get; set; } = 10;

    [ConfigurationKeyName("attempts")] public int Attempts { get; set; } = 3;

    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}