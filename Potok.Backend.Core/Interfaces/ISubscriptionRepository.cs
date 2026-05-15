using Potok.Backend.Core.Models.Database;

namespace Potok.Backend.Core.Interfaces;

public interface ISubscriptionRepository
{
    Task AddAsync(Subscription subscription);
    Task RemoveAsync(long tmdbId, string uid, string? media = null);
    Task<bool> ExistsAsync(long tmdbId, string uid, string? media = null);
}