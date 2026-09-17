using System.Net;

namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

/// <summary>
/// Detects a Cloudflare IUAM/challenge response.
/// Use <c>cf-mitigated</c>, not <c>cf-ray</c> — the latter is on every CF-proxied 200.
/// </summary>
public static class CloudflareChallenge
{
    private const int MaxBodyLength = 200_000;

    public static bool IsChallenge(HttpResponseMessage? response)
    {
        if (response is null)
            return false;

        if (response.StatusCode is not HttpStatusCode.Forbidden and not HttpStatusCode.ServiceUnavailable)
            return false;

        return response.Headers.TryGetValues("cf-mitigated", out _);
    }

    public static bool IsChallengeBody(string? body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength)
            return false;

        return body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
               || body.Contains("cf_chl_opt", StringComparison.OrdinalIgnoreCase)
               || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
               || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
               || body.Contains("Один момент", StringComparison.OrdinalIgnoreCase);
    }
}
