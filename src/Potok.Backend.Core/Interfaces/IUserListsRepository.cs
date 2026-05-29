using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Potok.Backend.Core.Entities;

namespace Potok.Backend.Core.Interfaces;

public interface IUserListsRepository
{
    // Favorites
    Task<IEnumerable<UserListEntry>> GetFavoritesAsync(Guid userId);
    Task<bool> IsFavoriteAsync(Guid userId, string tmdbId, string mediaType);
    Task AddFavoriteAsync(UserListEntry entry);
    Task RemoveFavoriteAsync(Guid userId, string tmdbId, string mediaType);

    // Watchlist
    Task<IEnumerable<UserListEntry>> GetWatchlistAsync(Guid userId);
    Task<bool> IsInWatchlistAsync(Guid userId, string tmdbId, string mediaType);
    Task AddToWatchlistAsync(UserListEntry entry);
    Task RemoveFromWatchlistAsync(Guid userId, string tmdbId, string mediaType);
}
