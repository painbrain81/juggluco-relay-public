using System.Buffers.Binary;
using System.IO.Hashing;

namespace JugglucoRelay.Storage;

/// <summary>A command received from the source phone, to be replayed as is to the receiving phones.</summary>
public sealed record JournalEntry(long Offset, long Next, bool AskAck, byte[] Command);

/// <summary>
/// Append-only journal of the received commands, in order of arrival. Each receiving phone
/// has a journal position and replays the journal from there: the same flow the source phone
/// sent to the relay, hence the same final state.
/// Entry format: int32 length, int32 flags (bit 0 = asks for ack), uint32 CRC32 of the
/// command, command, zeros up to a multiple of 4. An incomplete or corrupt entry at the tail
/// (power loss mid-write) is truncated on open.
/// </summary>
public sealed class Journal : IDisposable
{
    private const int HeaderSize = 12;
    private const int MaxEntry = 256 * 1024 * 1024;

    private readonly string path;
    private readonly FileStream writer;
    private readonly Lock gate = new();
    private TaskCompletionSource appended = NewSignal();
    private long end;

    public Journal(string path)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        end = ScanValidEnd();
        if (writer.Length != end)
        {
            writer.SetLength(end);
            writer.Flush(flushToDisk: true);
        }
        writer.Position = end;
    }

    public long End
    {
        get { lock (gate) return end; }
    }

    /// <summary>Appends the commands and flushes them to disk before returning.</summary>
    public void Append(IReadOnlyList<(byte[] Command, bool AskAck)> commands)
    {
        if (commands.Count == 0) return;
        TaskCompletionSource toSignal;
        lock (gate)
        {
            foreach (var (command, askAck) in commands)
            {
                var header = new byte[HeaderSize];
                BinaryPrimitives.WriteInt32LittleEndian(header, command.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), askAck ? 1 : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Crc32.HashToUInt32(command));
                writer.Write(header);
                writer.Write(command);
                writer.Write(new byte[Pad(command.Length)]);
            }
            writer.Flush(flushToDisk: true);
            end = writer.Position;
            toSignal = appended;
            appended = NewSignal();
        }
        toSignal.TrySetResult();
    }

    /// <summary>Completes when the journal grows beyond <paramref name="knownEnd"/>.</summary>
    public Task WaitBeyondAsync(long knownEnd, CancellationToken ct)
    {
        Task signal;
        lock (gate)
        {
            if (end > knownEnd) return Task.CompletedTask;
            signal = appended.Task;
        }
        return signal.WaitAsync(ct);
    }

    /// <summary>Reads the entries from <paramref name="offset"/> up to the current end.</summary>
    public IEnumerable<JournalEntry> ReadFrom(long offset)
    {
        long limit = End;
        if (offset < 0 || offset > limit) throw new ArgumentOutOfRangeException(nameof(offset));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = offset;
        var header = new byte[HeaderSize];
        while (fs.Position < limit)
        {
            long at = fs.Position;
            fs.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            bool askAck = (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) & 1) != 0;
            var command = new byte[length];
            fs.ReadExactly(command);
            fs.Position += Pad(length);
            yield return new JournalEntry(at, fs.Position, askAck, command);
        }
    }

    private long ScanValidEnd()
    {
        writer.Position = 0;
        var header = new byte[HeaderSize];
        long valid = 0;
        while (true)
        {
            if (writer.Length - valid < HeaderSize) return valid;
            writer.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxEntry || writer.Length - valid - HeaderSize < length + Pad(length)) return valid;
            var command = new byte[length];
            writer.ReadExactly(command);
            if (Crc32.HashToUInt32(command) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))) return valid;
            writer.Position += Pad(length);
            valid = writer.Position;
        }
    }

    private static int Pad(int length) => (4 - (length & 3)) & 3;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => writer.Dispose();
}
