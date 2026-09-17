using System.Text.RegularExpressions;

namespace Potok.Backend.Core.Utils;

public static class CacheKeyBuilder
{
    private static readonly Regex CollapseWhitespace = new("\\s+", RegexOptions.Compiled);

    public static string Build(string prefix, params string?[] parts)
    {
        var normalized = parts.Select(NormalizePart);
        return string.IsNullOrWhiteSpace(prefix)
            ? string.Join(":", normalized)
            : $"{NormalizePart(prefix)}:{string.Join(":", normalized)}";
    }

    public static string NormalizePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = CollapseWhitespace.Replace(value.Trim(), " ")
            .Replace(":", "_")
            .ToLowerInvariant();

        return normalized;
    }

    public static string NormalizeCategory(Dictionary<string, string>? category)
    {
        if (category == null || category.Count == 0)
            return "none";

        return string.Join(",",
            category.OrderBy(kv => kv.Key)
                .Select(kv => $"{NormalizePart(kv.Key)}:{NormalizePart(kv.Value)}"));
    }
}