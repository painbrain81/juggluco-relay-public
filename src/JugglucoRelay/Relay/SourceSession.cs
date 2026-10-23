using System.Buffers.Binary;
using System.Text;
using JugglucoRelay.Protocol;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Logging;

namespace JugglucoRelay.Relay;

/// <summary>
/// Receives the commands from the source phone: applies them to the files, appends them to the
/// journal for forwarding and answers acks and queries. Formats in docs/protocol/spec-2-receive.md §2.
/// The ack (sack) is sent only after files and journal are on disk: to the phone
/// "acked" means it will never have to resend that data.
/// </summary>
public sealed class SourceSession(
    string hostName,
    FrameChannel channel,
    FileStore files,
    Journal journal,
    HostStateStore states,
    Action onDataStored,
    ConnectionProgress progress,
    ILogger logger)
{
    private const int MaxAskFile = 16 * 1024 * 1024;

    private readonly List<(byte[] Command, bool AskAck)> pending = [];
    private readonly SortedDictionary<ushort, int> seen = [];
    private bool storedData;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] frame;
            progress.Stage = "waiting for commands from the phone";
            try
            {
                frame = await channel.ReadFrameAsync(ct);
            }
            catch (EndOfStreamException)
            {
                // Summary of the session's commands: a connection with just a sack (12) is the
                // reachability and password probe the app makes before using TCP/IP.
                logger.LogInformation("{Host}: connection closed by the phone; commands received: {Seen}", hostName,
                    seen.Count == 0 ? "none" : string.Join(", ", seen.Select(kv => $"{kv.Key}x{kv.Value}")));
                return;
            }
            progress.Stage = $"processing a frame of {frame.Length} bytes";
            await ProcessAsync(frame, ct);
        }
    }

    /// <summary>Interprets a frame. An unknown or malformed command closes the connection, as in Juggluco.</summary>
    public async Task ProcessAsync(byte[] buf, CancellationToken ct)
    {
        int it = 0;
        while (it < buf.Length)
        {
            if (buf.Length - it < 4) throw new ProtocolException($"Command truncated at {it}");
            var command = (Command)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(it));
            seen[(ushort)command] = seen.GetValueOrDefault((ushort)command) + 1;
            int length = command switch
            {
                Command.FileOnce => HandleFileOnce(buf, it),
                Command.Nums => HandleNums(buf, it),
                Command.NumNr => HandleNumNr(buf, it),
                Command.NumInit => HandleNumInit(buf, it),
                Command.Render or Command.ResetDevices or Command.Glucose or Command.StartSendCalibrate
                    => Relay(buf, it, 4),
                Command.Ack => await HandleAckAsync(ct),
                Command.AskLastNum => await HandleAskLastNumAsync(buf, it, ct),
                Command.AskFile => await HandleAskFileAsync(buf, it, ct),
                Command.UpToDate => HandleUpToDate(),
                Command.Backup or Command.WakeupStream => 4,
                Command.BackupStop => throw new ProtocolException("sbackupstop: the phone is closing the session"),
                _ => throw new ProtocolException($"Unhandled command: {(ushort)command} ({command})"),
            };
            it = Wire.Align4(it + length);
        }
        Commit();
    }

    private int HandleFileOnce(byte[] buf, int it)
    {
        var s = Slice(buf, it, 12);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        int nr = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
        int namePos = 12 + 8 * nr;
        if (nameLength < 1 || total < namePos + nameLength || total > buf.Length - it)
            throw new ProtocolException($"malformed sfileonce: totlen={total} nr={nr} namelen={nameLength}");

        var cmd = buf.AsSpan(it, total);
        var nameBytes = cmd.Slice(namePos, nameLength);
        int nul = nameBytes.IndexOf((byte)0);
        var name = Encoding.UTF8.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes);

        var segments = new List<(long, ReadOnlyMemory<byte>)>(nr);
        int dataPos = namePos + nameLength;
        for (int i = 0; i < nr; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(cmd[(12 + 8 * i)..]);
            int len = BinaryPrimitives.ReadInt32LittleEndian(cmd[(16 + 8 * i)..]);
            if (offset < 0 || len < 0 || len > total - dataPos)
                throw new ProtocolException($"sfileonce {name}: segment {offset}+{len} outside the command");
            segments.Add((offset, buf.AsMemory(it + dataPos, len)));
            dataPos += len;
        }

        if (files.Resolve(name) is null) throw new ProtocolException($"sfileonce: path rejected \"{name}\"");
        files.WriteSegments(name, segments);
        logger.LogDebug("{Host}: {File} {Segments} segments, dowith=0x{Dowith:X4}", hostName, name, nr,
            BinaryPrimitives.ReadUInt16LittleEndian(cmd[10..]));
        pending.Add((cmd.ToArray(), false));
        storedData = true;
        return total;
    }

    private int HandleNums(byte[] buf, int it)
    {
        var s = Slice(buf, it, 16);
        int dbase = s[2];
        int nr = s[3];
        int total = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        uint last = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        if (dbase is not (0 or 1) || total < 16 || total > buf.Length - it)
            throw new ProtocolException($"malformed snums: dbase={dbase} totlen={total}");

        var cmd = buf.AsSpan(it, total);
        var blocks = new List<(uint, ReadOnlyMemory<byte>)>(nr);
        int pos = 16;
        for (int i = 0; i < nr; i++)
        {
            if (total - pos < 8) throw new ProtocolException("snums: truncated block");
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(cmd[pos..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(cmd[(pos + 4)..]);
            pos += 8;
            if (count < 0 || count > (total - pos) / 16) throw new ProtocolException("snums: records beyond the command");
            blocks.Add((index, buf.AsMemory(it + pos, count * 16)));
            pos += count * 16;
        }
        files.WriteNums(dbase, blocks, last);
        pending.Add((cmd.ToArray(), false));
        storedData = true;
        return total;
    }

    private int HandleNumNr(byte[] buf, int it)
    {
        var s = Slice(buf, it, 8);
        int dbase = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        if (dbase is not (0 or 1)) throw new ProtocolException($"snumnr: dbase {dbase}");
        files.SetNumsLastPos(dbase, BinaryPrimitives.ReadUInt32LittleEndian(s[4..]));
        return Relay(buf, it, 8);
    }

    private int HandleNumInit(byte[] buf, int it)
    {
        var s = Slice(buf, it, 16);
        long ident = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);
        files.SetNumsFirstPos(ident != 0 ? 1 : 0, BinaryPrimitives.ReadUInt32LittleEndian(s[4..]));
        return Relay(buf, it, 16);
    }

    private int Relay(byte[] buf, int it, int length)
    {
        pending.Add((Slice(buf, it, length).ToArray(), false));
        return length;
    }

    private async Task<int> HandleAckAsync(CancellationToken ct)
    {
        if (pending.Count > 0) pending[^1] = (pending[^1].Command, true);
        Commit();
        await channel.WriteAckAsync(ct);
        return 4;
    }

    private async Task<int> HandleAskLastNumAsync(byte[] buf, int it, CancellationToken ct)
    {
        int dbase = BinaryPrimitives.ReadUInt16LittleEndian(Slice(buf, it, 4)[2..]);
        if (dbase is not (0 or 1)) throw new ProtocolException($"sasklastnum: dbase {dbase}");
        var reply = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, files.GetNumsLastPos(dbase));
        await channel.WriteFrameAsync(reply, askAck: false, ct);
        return 4;
    }

    private async Task<int> HandleAskFileAsync(byte[] buf, int it, CancellationToken ct)
    {
        var s = Slice(buf, it, 12);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        var nameBytes = Slice(buf, it + 12, nameLength);
        int nul = nameBytes.IndexOf((byte)0);
        var name = Encoding.UTF8.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes);
        if (length > MaxAskFile) throw new ProtocolException($"saskfile: length {length} too large");

        var (read, data) = files.ReadRange(name, offset, (int)length);
        var reply = new byte[Wire.Align4(4 + (int)length)];
        BinaryPrimitives.WriteInt32LittleEndian(reply, read);
        if (read > 0) data.AsSpan(0, read).CopyTo(reply.AsSpan(4));
        await channel.WriteFrameAsync(reply, askAck: false, ct);
        logger.LogDebug("{Host}: saskfile {File} {Offset}+{Length} -> {Read}", hostName, name, offset, length, read);
        return 12 + nameLength;
    }

    private int HandleUpToDate()
    {
        states.Update(hostName, s => s.LastData = DateTimeOffset.UtcNow);
        return 4;
    }

    private void Commit()
    {
        journal.Append(pending);
        pending.Clear();
        if (storedData)
        {
            storedData = false;
            onDataStored();
        }
    }

    private static ReadOnlySpan<byte> Slice(byte[] buf, int it, int length)
    {
        if (length < 0 || length > buf.Length - it)
            throw new ProtocolException($"Command truncated: {length} bytes needed at {it}, {buf.Length - it} available");
        return buf.AsSpan(it, length);
    }
}
