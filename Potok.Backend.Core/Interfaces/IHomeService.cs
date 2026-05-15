using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

public interface IHomeService
{
    Task<HomeResponse> GetHomeFeedAsync(string? cursor = null, string? baseUrl = null, string posterSize = "w780", string backdropSize = "w1280", string logoSize = "original");
}
