using System;
using System.Threading.Tasks;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface ITraktTokenService
{
    /// <summary>
    /// Returns a usable access token, refreshing when expired or within the skew window
    /// (or when <paramref name="forceRefresh"/> is set, e.g. after a Trakt 401).
    /// Returns null when there is no row or refresh fails (the row is deleted on a failed refresh).
    /// </summary>
    Task<string?> GetValidAccessTokenAsync(Guid userId, bool forceRefresh = false);
}
