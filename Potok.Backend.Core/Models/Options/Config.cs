using Microsoft.Extensions.Configuration;
using Potok.Backend.Core.Models.Options.TrackerConfigs;

namespace Potok.Backend.Core.Models.Options;

/// <summary>
///     Глобальная конфигурация приложения (обычно из config.yml).
/// </summary>
public class Config
{
    /// <summary>
    ///     IP-адрес для прослушивания входящих соединений (например, "0.0.0.0" или "127.0.0.1").
    ///     Значение "any" означает прослушивание всех интерфейсов.
    /// </summary>
    [ConfigurationKeyName("listen-ip")]
    public string ListenIp { get; set; } = "any";

    /// <summary>
    ///     Порт для запуска веб-сервера.
    /// </summary>
    [ConfigurationKeyName("listen-port")]
    public int ListenPort { get; set; } = 9117;

    /// <summary>
    ///     API-ключ для защиты доступа к методам API.
    ///     Если не задан, доступ открыт (или ограничен другими способами).
    /// </summary>
    [ConfigurationKeyName("api-key")]
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Настройки обновления торрентов
    /// </summary>
    [ConfigurationKeyName("refresh")]
    public RefreshSettings Refresh { get; set; } = new();

    /// <summary>
    ///     Максимальное количество результатов в выдаче
    /// </summary>
    [ConfigurationKeyName("max-result-count")]
    public int MaxResultCount { get; set; } = 250;

    /// <summary>
    ///     Включить объединение дубликатов раздач (по InfoHash) в результатах поиска.
    /// </summary>
    [ConfigurationKeyName("merge-duplicates")]
    public bool MergeDuplicates { get; set; } = true;

    // Merge duplicates for NUM-style requests (same behavior as legacy).
    [ConfigurationKeyName("merge-num-duplicates")]
    public bool MergeNumDuplicates { get; set; } = true;

    [ConfigurationKeyName("ffprobe")] public Ffprobe Ffprobe { get; set; } = new();

    /// <summary>
    ///     Настройки прокси-серверов для исходящих запросов к трекерам.
    /// </summary>
    [ConfigurationKeyName("proxy")]
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>
    ///     Настройки кеша
    /// </summary>
    [ConfigurationKeyName("cache")]
    public Cache Cache { get; set; } = new();

    /// <summary>
    ///     Настройки для RuTracker (авторизация и т.д.).
    /// </summary>
    [ConfigurationKeyName("rutracker")]
    public RuTrackerSettings RuTracker { get; set; } = new();

    /// <summary>
    ///     Настройки для AnimeLayer.
    /// </summary>
    [ConfigurationKeyName("animelayer")]
    public AnimeLayerSettings AnimeLayer { get; set; } = new();

    /// <summary>
    ///     Настройки для NNMClub.
    /// </summary>
    [ConfigurationKeyName("nnmclub")]
    public NNMClubSettings NNMClub { get; set; } = new();

    /// <summary>
    ///     Настройки для RuTor.
    /// </summary>
    [ConfigurationKeyName("rutor")]
    public RuTorSettings RuTor { get; set; } = new();

    /// <summary>
    ///     Настройки для Aniliberty.
    /// </summary>
    [ConfigurationKeyName("aniliberty")]
    public AnilibertySettings Aniliberty { get; set; } = new();

    /// <summary>
    ///     Настройки для Kinozal.
    /// </summary>
    [ConfigurationKeyName("kinozal")]
    public KinozalSettings Kinozal { get; set; } = new();

    /// <summary>
    ///     Настройки для MegaPeer.
    /// </summary>
    [ConfigurationKeyName("megapeer")]
    public MegaPeerSettings MegaPeer { get; set; } = new();
}