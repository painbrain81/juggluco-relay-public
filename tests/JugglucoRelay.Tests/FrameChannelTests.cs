using System.Buffers.Binary;
using JugglucoRelay.Crypto;
using JugglucoRelay.Protocol;

namespace JugglucoRelay.Tests;

public class FrameChannelTests
{
    private static readonly byte[] Key = JugglucoKey.FromPassword("secret1")!;
    private static readonly byte[] Nonce = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    [Fact]
    public async Task LargeFrame_ArrivingInSmallPieces_IsReadWhole()
    {
        var commands = new byte[300_001];
        new Random(7).NextBytes(commands);

        var wire = new MemoryStream();
        await new FrameChannel(wire, new AsconSession(Key, Nonce)).WriteFrameAsync(commands, askAck: false, default);

        var reader = new FrameChannel(new TrickleStream(wire.ToArray(), 1000), new AsconSession(Key, Nonce));
        Assert.Equal(commands, await reader.ReadFrameAsync(default));
    }

    [Fact]
    public async Task HugeDeclaredLength_IsNotAllocatedBeforeTheBytesArrive()
    {
        // Valid frame head announcing 200 MB, then the connection closes
        var plain = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(plain, 200 * 1024 * 1024);
        var cipher = new byte[16];
        var tag = new byte[AsconSession.TagLength];
        new AsconSession(Key, Nonce).Encrypt(plain, cipher, tag);

        var reader = new FrameChannel(new MemoryStream([.. tag, .. cipher]), new AsconSession(Key, Nonce));
        // MemoryStream completes synchronously: everything happens on this thread
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadFrameAsync(default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        var wire = new MemoryStream();
        await new FrameChannel(wire, new AsconSession(Key, Nonce)).WriteFrameAsync(new byte[100], askAck: true, default);

        var other = JugglucoKey.FromPassword("secret2")!;
        var reader = new FrameChannel(new MemoryStream(wire.ToArray()), new AsconSession(other, Nonce));
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadFrameAsync(default));
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, like a slow socket.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
    }
}
