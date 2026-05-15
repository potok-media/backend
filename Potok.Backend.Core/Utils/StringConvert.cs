using System.Text.RegularExpressions;

namespace Potok.Backend.Core.Utils;

public static class StringConvert
{
    public static string? SearchName(string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
            return null;

        val = val.ToLowerInvariant()
            .Replace("ё", "е")
            .Replace("щ", "ш");

        // Оставляем латиницу, кириллицу и цифры.
        val = Regex.Replace(val, "[^a-z0-9а-я]", "");

        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    public static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        double value = bytes;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    public static int ParseQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
            return 0;

        var match = Regex.Match(quality, "(\\d{3,4})p", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var q))
            return q;

        if (int.TryParse(quality, out var numeric))
            return numeric;

        return 0;
    }

    public static string ClearTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var cleared = Regex.Replace(title, @"[^a-zA-Zа-яА-ЯёЁ0-9\s]", " ");
        
        return Regex.Replace(cleared, @"\s+", " ").Trim();
    }
}
