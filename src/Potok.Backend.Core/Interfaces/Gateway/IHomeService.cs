using Potok.Backend.Core.Models.Gateway;

namespace Potok.Backend.Core.Interfaces.Gateway;

public interface IHomeService
{
    Task<HomeResponse> GetHomeFeedAsync(
        string? baseUrl = null,
        string posterSize = "w780",
        string backdropSize = "w1280",
        string logoSize = "original");
}