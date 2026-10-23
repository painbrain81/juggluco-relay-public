using System.Buffers.Binary;
using JugglucoRelay.Crypto;

namespace JugglucoRelay.Protocol;

/// <summary>
/// Encrypted frames of the mirror protocol on an already authenticated connection
/// (docs/protocol/spec-1-connection.md §2.4). A frame on the wire is
/// <c>tag(16) || encrypted(int32 length || commands || [pad || sack] || zeros up to 16)</c>.
/// Acks (sack and reply) instead always travel unencrypted and do not touch the Ascon state.
/// </summary>
public sealed class FrameChannel(Stream stream, AsconSession session)
{
    private const int MinPlaintext = 16;
    private const int InitialBuffer = 64 * 1024;

    /// <summary>True if the phone has already sent bytes not yet read.</summary>
    public bool DataAvailable => stream is System.Net.Sockets.NetworkStream ns && ns.DataAvailable;

    /// <summary>Reads a frame and returns just the command bytes. Wrong tag: exception.</summary>
    public async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var head = new byte[AsconSession.TagLength + MinPlaintext];
        await stream.ReadExactlyAsync(head, ct);
        var tag = head.AsMemory(0, AsconSession.TagLength);
        var firstBlock = head.AsMemory(AsconSession.TagLength, MinPlaintext);

        var peek = new byte[MinPlaintext];
        session.PeekFirstBlock(firstBlock.Span, peek);
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(peek);
        if (dataLength < 0 || dataLength > Wire.MaxFrameData)
            throw new ProtocolException($"Invalid frame length: {dataLength}");

        int total = Math.Max(MinPlaintext, dataLength + 4);
        var cipher = await ReadRestAsync(firstBlock, total, ct);

        // Decrypt reads each encrypted block before writing its plaintext, so it can decrypt in place
        if (!session.Decrypt(cipher, tag.Span, cipher))
            throw new ProtocolException("Invalid tag: different password or corrupted data");
        return cipher.AsSpan(4, dataLength).ToArray();
    }

    /// <summary>
    /// Reads the rest of the frame, growing the buffer as the bytes arrive. The length at the
    /// head can only be verified once the frame is complete: without the password one gets a
    /// random one, up to 256 MB, and allocating it upfront would be enough to exhaust memory.
    /// </summary>
    private async Task<byte[]> ReadRestAsync(ReadOnlyMemory<byte> firstBlock, int total, CancellationToken ct)
    {
        var buf = new byte[Math.Min(total, InitialBuffer)];
        firstBlock.CopyTo(buf);
        int filled = firstBlock.Length;
        while (filled < total)
        {
            if (filled == buf.Length)
                Array.Resize(ref buf, (int)Math.Min(total, 2L * buf.Length));
            int n = await stream.ReadAsync(buf.AsMemory(filled), ct);
            if (n == 0) throw new EndOfStreamException();
            filled += n;
        }
        return buf;
    }

    /// <summary>Writes a frame in a single write, as Juggluco recommends (the reader does not reassemble the tag).</summary>
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> commands, bool askAck, CancellationToken ct)
    {
        int haveLength = 4 + commands.Length;
        int gap = 0, commandLength = commands.Length;
        if (askAck)
        {
            gap = Wire.Align4(haveLength) - haveLength;
            haveLength += gap + 4;
            commandLength = commands.Length + gap + 4;
        }
        int total = Math.Max(MinPlaintext, haveLength);

        var plain = new byte[total];
        BinaryPrimitives.WriteInt32LittleEndian(plain, commandLength);
        commands.Span.CopyTo(plain.AsSpan(4));
        if (askAck) Wire.AckRequest.CopyTo(plain.AsSpan(4 + commands.Length + gap));

        var wire = new byte[AsconSession.TagLength + total];
        session.Encrypt(plain, wire.AsSpan(AsconSession.TagLength), wire.AsSpan(0, AsconSession.TagLength));
        await stream.WriteAsync(wire, ct);
        await stream.FlushAsync(ct);
    }

    public async Task WriteAckAsync(CancellationToken ct)
    {
        await stream.WriteAsync(Wire.AckReply.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    public async Task ReadAckAsync(CancellationToken ct)
    {
        var buf = new byte[4];
        await stream.ReadExactlyAsync(buf, ct);
        if (!buf.AsSpan().SequenceEqual(Wire.AckReply))
            throw new ProtocolException($"Unexpected ack: {Convert.ToHexString(buf)}");
    }
}

public sealed class ProtocolException(string message) : Exception(message);
