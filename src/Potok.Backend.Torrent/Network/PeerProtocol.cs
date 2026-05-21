using System.Buffers;

namespace Potok.Backend.Torrent.Network;

public static class PeerProtocol {
    public const int HandshakeLength = 68;

    public static void WriteHandshake(IBufferWriter<byte> writer, Handshake handshake) {
        if (handshake.InfoHash.Length != 20 || handshake.PeerId.Length != 20)
            throw new ArgumentException("InfoHash and PeerId must be 20 bytes long.");

        var span = writer.GetSpan(HandshakeLength);
        span[0] = 19;
        "BitTorrent protocol"u8.CopyTo(span.Slice(1));
        span.Slice(20, 8).Clear(); // Reserved bytes (0)
        handshake.InfoHash.Span.CopyTo(span.Slice(28));
        handshake.PeerId.Span.CopyTo(span.Slice(48));
        writer.Advance(HandshakeLength);
    }

    public static Handshake ReadHandshake(ReadOnlySpan<byte> data) {
        if (data.Length < HandshakeLength) throw new ArgumentException("Data too short for handshake");
        if (data[0] != 19 || !data.Slice(1, 19).SequenceEqual("BitTorrent protocol"u8))
            throw new FormatException("Invalid handshake protocol");
            
        return new Handshake(data.Slice(28, 20).ToArray(), data.Slice(48, 20).ToArray());
    }
}
