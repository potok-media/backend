namespace Potok.Backend.Torrent.Network;

public record struct Handshake(ReadOnlyMemory<byte> InfoHash, ReadOnlyMemory<byte> PeerId);
