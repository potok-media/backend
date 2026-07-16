using System;
using System.Threading.Tasks;
using Potok.Backend.Core.Entities.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByTelegramIdAsync(long telegramId);
    Task CreateAsync(User user);
    Task UpdateSyncStrategyAsync(Guid userId, string strategy);
    Task UpdatePasswordAsync(Guid userId, string passwordHash);
    Task SetCredentialsAsync(Guid userId, string username, string passwordHash);
    Task LinkTelegramAsync(Guid userId, long telegramId, string? telegramUsername);
    Task UnlinkTelegramAsync(Guid userId);
    Task<UserTraktToken?> GetTraktTokenAsync(Guid userId);
    Task SaveTraktTokenAsync(UserTraktToken token);
    Task DeleteTraktTokenAsync(Guid userId);
}
