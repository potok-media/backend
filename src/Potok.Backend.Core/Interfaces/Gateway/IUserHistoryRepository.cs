using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Potok.Backend.Core.Entities.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface IUserHistoryRepository
{
    Task<IEnumerable<UserHistory>> GetByUserIdAsync(Guid userId);
    Task<UserHistory?> GetProgressAsync(Guid userId, string tmdbId, string mediaType, int? season = null, int? episode = null);
    Task SaveProgressAsync(UserHistory history);
    Task DeleteProgressAsync(Guid userId, string tmdbId, string mediaType, int? season = null, int? episode = null);
}
