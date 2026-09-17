using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Enums;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Details;
using Potok.Backend.Core.Models.SearchEngine.Options;
using Potok.Backend.Infrastructure.Http;

namespace Potok.Backend.Infrastructure.SearchEngine.Services.Trackers;

public abstract class BaseTrackerSearch : ITrackerRefreshProvider
{
    protected static readonly Encoding RuEncoding = Encoding.GetEncoding("windows-1251");
    protected readonly ICacheService CacheService;
    protected readonly Config Config;
    protected readonly TrackerHttpClient HttpService;

    protected BaseTrackerSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
    {
        HttpService = httpService;
        CacheService = cacheService;
        Config = config.Value;
    }

    public abstract TrackerType Tracker { get; }
    public abstract string TrackerName { get; }
    public abstract string Host { get; }

    public virtual Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<TorrentDetails>>([]);
    }

    public virtual Task InvokeAsync()
    {
        return Task.CompletedTask;
    }

    protected static long ParseSize(string val, string unit)
    {
        if (!double.TryParse(val.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return 0;

        var multiplier = unit.ToUpperInvariant() switch
        {
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "MB" => 1024d * 1024d,
            "KB" => 1024d,
            _ => 1d
        };

        return (long)(value * multiplier);
    }

    protected static DateTime ParseDate(string d, string m, string y)
    {
        if (!int.TryParse(d, out var day)) day = 1;
        if (!int.TryParse(y, out var year)) year = 0;

        year += 2000;

        var month = m.ToLowerInvariant() switch
        {
            "янв" => 1,
            "фев" => 2,
            "мар" => 3,
            "апр" => 4,
            "май" => 5,
            "июн" => 6,
            "июл" => 7,
            "авг" => 8,
            "сен" => 9,
            "окт" => 10,
            "ноя" => 11,
            "дек" => 12,
            _ => 1
        };

        try
        {
            return new DateTime(year, month, day);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}