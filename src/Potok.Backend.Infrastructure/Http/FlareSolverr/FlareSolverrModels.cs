using System.Text.Json.Serialization;

namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

public sealed record FlareSolverrProxy(string Url, string? Username, string? Password);

public sealed record FlareSolverrCookie(string Name, string Value);

public sealed record FlareSolverrSolution(int Status, string? Html, IReadOnlyList<FlareSolverrCookie> Cookies);

internal sealed class FlareSolverrRequest
{
    [JsonPropertyName("cmd")]
    public required string Cmd { get; init; }

    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("maxTimeout")]
    public int? MaxTimeout { get; init; }

    [JsonPropertyName("postData")]
    public string? PostData { get; init; }

    [JsonPropertyName("cookies")]
    public List<FlareSolverrCookieDto>? Cookies { get; init; }

    [JsonPropertyName("proxy")]
    public FlareSolverrProxyDto? Proxy { get; init; }
}

internal sealed class FlareSolverrCookieDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class FlareSolverrProxyDto
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

internal sealed class FlareSolverrApiResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("solution")]
    public FlareSolverrApiSolution? Solution { get; set; }

    [JsonPropertyName("sessions")]
    public List<string>? Sessions { get; set; }
}

internal sealed class FlareSolverrApiSolution
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("cookies")]
    public List<FlareSolverrCookieDto>? Cookies { get; set; }
}

internal static class FlareSolverrCookieParser
{
    public static List<FlareSolverrCookieDto> ParseHeader(string? cookieHeader)
    {
        var list = new List<FlareSolverrCookieDto>();
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return list;

        foreach (var part in cookieHeader.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length > 0)
                list.Add(new FlareSolverrCookieDto { Name = name, Value = value });
        }

        return list;
    }

    public static IReadOnlyList<FlareSolverrCookie> ToCookies(IEnumerable<FlareSolverrCookieDto>? source)
    {
        if (source is null)
            return [];

        return source
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new FlareSolverrCookie(c.Name, c.Value ?? ""))
            .ToArray();
    }
}
