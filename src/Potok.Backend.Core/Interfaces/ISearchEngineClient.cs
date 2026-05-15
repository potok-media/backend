using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

public interface ISearchEngineClient
{
    Task<TorrentSearchResponse> SearchAsync(TorrentSearchRequest request);
}
