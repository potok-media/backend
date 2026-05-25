using System;
using System.Threading.Tasks;
using Potok.Backend.Core.Entities;

namespace Potok.Backend.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByUsernameAsync(string username);
    Task CreateAsync(User user);
    Task UpdateSyncStrategyAsync(Guid userId, string strategy);
}
