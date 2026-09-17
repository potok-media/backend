using Potok.Backend.Core.Models.SearchEngine.Options;

namespace Potok.Backend.Core.Enums;

public enum TrackerType
{
    Anifilm = 0,
    Aniliberty = 1,
    AnimeLayer = 2,
    Baibako = 3,
    Bitru = 4,
    Kinozal = 5,
    Lostfilm = 6,
    Megapeer = 7,
    NNMClub = 8,
    Rezka = 9,
    Rutor = 10,
    Rutracker = 11,
    Selezen = 12,
    Toloka = 13,
    TorrentBy = 14
}

public static class TrackerTypeExtension 
{
    public static bool IsSearchEnabled(this TrackerType type, Config config)
    {
        return type switch
        {
            TrackerType.Rutracker => config.RuTracker.EnableSearch,
            TrackerType.AnimeLayer => config.AnimeLayer.EnableSearch,
            TrackerType.NNMClub => config.NNMClub.EnableSearch,
            TrackerType.Rutor => config.RuTor.EnableSearch,
            TrackerType.Aniliberty => config.Aniliberty.EnableSearch,
            TrackerType.Kinozal => config.Kinozal.EnableSearch,
            _ => true
        };
    }
}