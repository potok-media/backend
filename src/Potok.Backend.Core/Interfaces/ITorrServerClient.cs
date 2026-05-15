using Potok.Backend.Core.Models;

namespace Potok.Backend.Core.Interfaces;

public interface ITorrServerClient
{
    Task<TorrentFilesResponse> GetFilesAsync(TorrentFilesRequest request);
    Task<TorrentStreamResponse> GetStreamUrlAsync(TorrentStreamRequest request);
    Task<IEnumerable<string>> GetNormalizedStreamUrlsAsync(TorrentFilesRequest request);
}
