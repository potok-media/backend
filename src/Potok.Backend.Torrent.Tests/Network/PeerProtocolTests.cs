using System.Buffers;
using Potok.Backend.Torrent.Network;
using Xunit;

namespace Potok.Backend.Torrent.Tests.Network;

public class PeerProtocolTests {
    [Fact]
    public void WriteHandshake_ShouldWriteCorrectBytes() {
        var infoHash = new byte[20];
        Array.Fill(infoHash, (byte)1);
        var peerId = new byte[20];
        Array.Fill(peerId, (byte)2);
        var handshake = new Handshake(infoHash, peerId);
        var writer = new ArrayBufferWriter<byte>();

        PeerProtocol.WriteHandshake(writer, handshake);

        var result = writer.WrittenSpan;
        Assert.Equal(68, result.Length);
        Assert.Equal(19, result[0]);
        Assert.Equal("BitTorrent protocol"u8.ToArray(), result.Slice(1, 19).ToArray());
        Assert.All(result.Slice(20, 8).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(infoHash, result.Slice(28, 20).ToArray());
        Assert.Equal(peerId, result.Slice(48, 20).ToArray());
    }

    [Fact]
    public void ReadHandshake_ShouldReturnCorrectHandshake() {
        var data = new byte[68];
        data[0] = 19;
        "BitTorrent protocol"u8.CopyTo(data.AsSpan(1));
        var infoHash = new byte[20];
        Array.Fill(infoHash, (byte)3);
        infoHash.CopyTo(data.AsSpan(28));
        var peerId = new byte[20];
        Array.Fill(peerId, (byte)4);
        peerId.CopyTo(data.AsSpan(48));

        var handshake = PeerProtocol.ReadHandshake(data);

        Assert.Equal(infoHash, handshake.InfoHash.ToArray());
        Assert.Equal(peerId, handshake.PeerId.ToArray());
    }

    [Fact]
    public void ReadHandshake_ShouldThrowOnInvalidProtocol() {
        var data = new byte[68];
        data[0] = 19;
        "Invalid protocol   "u8.CopyTo(data.AsSpan(1));
        
        Assert.Throws<FormatException>(() => PeerProtocol.ReadHandshake(data));
    }
}
