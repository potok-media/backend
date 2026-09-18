namespace Potok.Backend.Infrastructure.Http.FlareSolverr;

public interface IFlareSolverrClient
{
    Task<FlareSolverrSolution?> GetAsync(
        string url,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct);

    Task<FlareSolverrSolution?> PostAsync(
        string url,
        string? postData,
        string? cookieHeader,
        FlareSolverrProxy? proxy,
        CancellationToken ct);

    Task<bool> WarmupAsync(string url, CancellationToken ct);

    Task<bool> EnsureSessionAsync(CancellationToken ct);
}
