using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Potok.Backend.Core.Entities;

namespace Potok.Backend.Core.Interfaces;

public interface IInfuseRepository
{
    Task EnsureDatabaseAsync();
    Task<IEnumerable<InfuseLibraryItem>> GetAllAsync();
    Task<InfuseLibraryItem?> GetByTmdbIdAsync(long tmdbId);
    Task SaveAsync(InfuseLibraryItem item);
    Task DeleteAsync(Guid id);
    Task UpdateStatusAsync(Guid id, InfuseItemStatus status);
}
