using Potok.Backend.Core.Models.SearchEngine.Database;

namespace Potok.Backend.Core.Interfaces.SearchEngine;

public interface ISubscriptionRepository
{
    Task AddAsync(Subscription subscription);
    Task RemoveAsync(long tmdbId, string uid, string? media = null);
    Task<bool> ExistsAsync(long tmdbId, string uid, string? media = null);
}