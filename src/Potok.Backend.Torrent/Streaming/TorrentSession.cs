using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Potok.Backend.Torrent.Network;
using Potok.Backend.Torrent.Storage;

namespace Potok.Backend.Torrent.Streaming;

public record TorrentFile(string Path, long Length, long Offset);

public record TorrentMetadata(
    byte[] InfoHash,
    byte[][] PieceHashes,
    int PieceSize,
    long TotalSize,
    List<string> TrackerUrls,
    IReadOnlyList<TorrentFile> Files
);

public class TorrentSession {
    private readonly TorrentMetadata _metadata;
    public TorrentMetadata Metadata => _metadata;
    private readonly PieceStore _pieceStore;
    private readonly PieceDownloader _downloader = new();
    private readonly List<PeerState> _peers = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Memory<byte>>> _pieceWaiters = new();
    private readonly HashSet<int> _downloadedPieces = new();
    private readonly HashSet<int> _inProgressPieces = new();
    private readonly object _lock = new();

    public int CurrentPieceIndex { get; set; } = 0;

    public TorrentSession(TorrentMetadata metadata, PieceStore pieceStore) {
        _metadata = metadata;
        _pieceStore = pieceStore;
    }

    public async Task StartAsync(CancellationToken ct) {
        // 1. Discover peers
        var peerEndPoints = await DiscoverPeersAsync(ct);

        // 2. Connect to peers
        foreach (var ep in peerEndPoints) {
            _ = AddPeerAsync(ep, ct);
        }

        // 3. Main download loop
        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(1000, ct); // Orchestration interval

                lock (_lock) {
                    // Determine next pieces to download based on priority
                    var totalPieces = _metadata.PieceHashes.Length;
                    var piecesToDownload = Enumerable.Range(0, totalPieces)
                        .Select(i => new { Index = i, Priority = SlidingWindow.GetPriority(i, CurrentPieceIndex, totalPieces) })
                        .Where(p => p.Priority != PiecePriority.Ignore && !_downloadedPieces.Contains(p.Index) && !_inProgressPieces.Contains(p.Index))
                        .OrderBy(p => p.Priority) // Critical first, then Buffer, etc.
                        .ToList();

                    foreach (var piece in piecesToDownload) {
                        // Find an unchoked peer that has this piece and is available
                        var availablePeer = _peers.FirstOrDefault(p => 
                            !p.Connection.PeerChoking && 
                            p.HasPiece(piece.Index) && 
                            p.IsAvailable);
                        
                        if (availablePeer != null) {
                            _inProgressPieces.Add(piece.Index);
                            availablePeer.IsAvailable = false;
                            _ = DownloadPieceFromPeerAsync(availablePeer, piece.Index, ct);
                        }
                    }
                }
            } catch (OperationCanceledException) {
                break;
            } catch {
                // Ignore errors in the main loop to keep it running
            }
        }
    }

    private async Task<List<IPEndPoint>> DiscoverPeersAsync(CancellationToken ct) {
        var allPeers = new HashSet<IPEndPoint>();
        var peerId = new byte[20];
        Random.Shared.NextBytes(peerId);

        foreach (var trackerUrl in _metadata.TrackerUrls) {
            try {
                if (!Uri.TryCreate(trackerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "udp") continue;

                using var client = new UdpTrackerClient(uri.Host, uri.Port);
                var connectionId = await client.ConnectAsync(ct);
                var response = await client.AnnounceAsync(connectionId, _metadata.InfoHash, peerId, 6881, ct);
                
                foreach (var peer in response.Peers) {
                    if (IPAddress.TryParse(peer.Ip, out var ip)) {
                        allPeers.Add(new IPEndPoint(ip, peer.Port));
                    }
                }
            } catch {
                // Ignore tracker failures
            }
        }
        
        return allPeers.ToList();
    }

    public async Task AddPeerAsync(IPEndPoint endPoint, CancellationToken ct) {
        await ConnectAndProcessPeerAsync(endPoint, ct);
    }

    private async Task ConnectAndProcessPeerAsync(IPEndPoint endPoint, CancellationToken ct) {
        try {
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endPoint, ct);
            
            var connection = new PeerConnection(socket);
            var peerId = new byte[20];
            Random.Shared.NextBytes(peerId);
            var handshake = new Handshake(_metadata.InfoHash, peerId);
            
            await connection.ConnectAsync(handshake, ct);
            
            // Send interested message
            await connection.SendMessageAsync(new InterestedMessage(), ct);

            var peerState = new PeerState(connection, _metadata.PieceHashes.Length);
            lock (_lock) {
                _peers.Add(peerState);
            }

            // Start processing messages from the socket to the internal channel
            var processTask = connection.ProcessMessagesAsync(ct);
            
            // Start processing messages from the internal channel to PeerState and Downloader channel
            await peerState.ProcessMessagesAsync(ct);
            
            await processTask;
        } catch {
            // Peer disconnected or failed to connect
        } finally {
            // Clean up peer state
            lock (_lock) {
                // We might want to remove the peer from _peers here
            }
        }
    }

    private async Task DownloadPieceFromPeerAsync(PeerState peer, int pieceIndex, CancellationToken ct) {
        try {
            var pieceSize = (pieceIndex == _metadata.PieceHashes.Length - 1) 
                ? (int)(_metadata.TotalSize % _metadata.PieceSize) 
                : _metadata.PieceSize;
            
            if (pieceSize <= 0) pieceSize = _metadata.PieceSize;

            var success = await _downloader.DownloadPieceAsync(
                peer.Connection, 
                _pieceStore, 
                pieceIndex, 
                pieceSize, 
                _metadata.PieceHashes[pieceIndex], 
                ct,
                peer.DownloaderMessages);

            lock (_lock) {
                _inProgressPieces.Remove(pieceIndex);
                peer.IsAvailable = true;
                if (success) {
                    _downloadedPieces.Add(pieceIndex);
                    if (_pieceWaiters.TryRemove(pieceIndex, out var tcs)) {
                        if (_pieceStore.TryGetPiece(pieceIndex, out var data)) {
                            tcs.TrySetResult(data);
                        }
                    }
                }
            }
        } catch {
            lock (_lock) {
                _inProgressPieces.Remove(pieceIndex);
                peer.IsAvailable = true;
            }
        }
    }

    public Task<Memory<byte>> GetPieceAsync(int index, CancellationToken ct = default) {
        lock (_lock) {
            if (_downloadedPieces.Contains(index)) {
                if (_pieceStore.TryGetPiece(index, out var data)) {
                    return Task.FromResult(data);
                }
            }

            var tcs = _pieceWaiters.GetOrAdd(index, _ => new TaskCompletionSource<Memory<byte>>());
            return tcs.Task;
        }
    }

    private class PeerState {
        public PeerConnection Connection { get; }
        public BitArray Bitfield { get; }
        public bool IsAvailable { get; set; } = true;
        private readonly Channel<IPeerMessage> _downloaderChannel = Channel.CreateUnbounded<IPeerMessage>();
        public ChannelReader<IPeerMessage> DownloaderMessages => _downloaderChannel.Reader;

        public PeerState(PeerConnection connection, int totalPieces) {
            Connection = connection;
            Bitfield = new BitArray(totalPieces);
        }

        public void UpdateBitfield(ReadOnlySpan<byte> data) {
            for (int i = 0; i < data.Length; i++) {
                byte b = data[i];
                for (int j = 0; j < 8; j++) {
                    int index = i * 8 + j;
                    if (index < Bitfield.Length) {
                        Bitfield[index] = (b & (1 << (7 - j))) != 0;
                    }
                }
            }
        }

        public void UpdateBitfield(int index) {
            if (index >= 0 && index < Bitfield.Length) {
                Bitfield[index] = true;
            }
        }

        public async Task ProcessMessagesAsync(CancellationToken ct) {
            try {
                await foreach (var message in Connection.IncomingMessages.ReadAllAsync(ct)) {
                    if (message is BitfieldMessage bf) {
                        UpdateBitfield(bf.Data.Span);
                    } else if (message is HaveMessage have) {
                        UpdateBitfield(have.PieceIndex);
                    } else {
                        await _downloaderChannel.Writer.WriteAsync(message, ct);
                    }
                }
            } catch (OperationCanceledException) {
            } finally {
                _downloaderChannel.Writer.TryComplete();
            }
        }

        public bool HasPiece(int index) => index >= 0 && index < Bitfield.Length && Bitfield[index];
    }
}
