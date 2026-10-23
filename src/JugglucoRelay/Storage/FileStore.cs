using System.Buffers.Binary;

namespace JugglucoRelay.Storage;

/// <summary>
/// Copy of the source phone's files, with the same layout as <c>jugglucodata</c>
/// (settings.dat, sensors/…, nums/…). Files are written by offset, without interpreting them.
/// Unlike Juggluco, paths received from the network are checked: no absolute
/// paths and no <c>..</c>.
/// </summary>
public sealed class FileStore
{
    /// <summary>Maximum accepted size for a received file (the largest, data.dat, stays under a few MB).</summary>
    public const long MaxFileSize = 256L * 1024 * 1024;

    private const int NumsHeader = 256;
    private const int NumSize = 16;
    private const int LastPosOffset = 128;
    private const int FirstPosOffset = 144;
    private static readonly byte[] NumsMagic = [0xFE, 0xD2, 0xDE, 0xAD];

    private readonly string root;
    private readonly Lock gate = new();

    public FileStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public string Root => root;

    /// <summary>Absolute path of a name received from the network, or null if it is not acceptable.</summary>
    public string? Resolve(string relative)
    {
        if (relative.Length == 0 || relative.Length > 200) return null;
        if (relative[0] == '/' || relative.Contains('\\') || relative.Contains(':')) return null;
        foreach (var c in relative)
            if (char.IsControl(c)) return null;
        foreach (var part in relative.Split('/'))
            if (part.Length == 0 || part == "." || part == "..") return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Writes the (offset, bytes) segments to the file and flushes the data to disk.</summary>
    public void WriteSegments(string relative, IReadOnlyList<(long Offset, ReadOnlyMemory<byte> Data)> segments)
    {
        var path = Resolve(relative) ?? throw new IOException($"Path rejected: {relative}");
        foreach (var (offset, data) in segments)
            if (offset < 0 || offset + data.Length > MaxFileSize)
                throw new IOException($"Segment out of bounds in {relative}: {offset}+{data.Length}");
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            foreach (var (offset, data) in segments)
            {
                fs.Position = offset;
                fs.Write(data.Span);
            }
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>Reads up to <paramref name="length"/> bytes; -1 if the file does not exist (like Juggluco).</summary>
    public (int Read, byte[] Data) ReadRange(string relative, long offset, int length)
    {
        var path = Resolve(relative);
        var data = new byte[length];
        if (path is null || offset < 0) return (-1, data);
        lock (gate)
        {
            if (!File.Exists(path)) return (-1, data);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = offset;
            int total = 0;
            while (total < length)
            {
                int n = fs.Read(data, total, length - total);
                if (n == 0) break;
                total += n;
            }
            return (total, data);
        }
    }

    // --- Amounts (insulin, carbs…): two databases, files with a 256-byte header and 16-byte records.
    // Same layout as Juggluco (Juggluco nums/numdata.hpp:74-88).

    private static string NumsPath(int dbase) => dbase == 0 ? "nums/here/nums0" : "nums/watch/nums-1";

    public void WriteNums(int dbase, IReadOnlyList<(uint Index, ReadOnlyMemory<byte> Records)> blocks, uint lastPos)
    {
        var segments = new List<(long, ReadOnlyMemory<byte>)>(blocks.Count + 2);
        foreach (var (index, records) in blocks)
            segments.Add((NumsHeader + (long)index * NumSize, records));
        segments.Add((LastPosOffset, Int32Bytes(lastPos)));
        WriteNumsSegments(dbase, segments);
    }

    public void SetNumsLastPos(int dbase, uint lastPos) => WriteNumsSegments(dbase, [(LastPosOffset, Int32Bytes(lastPos))]);

    public void SetNumsFirstPos(int dbase, uint firstPos) => WriteNumsSegments(dbase, [(FirstPosOffset, Int32Bytes(firstPos))]);

    public uint GetNumsLastPos(int dbase)
    {
        var (read, data) = ReadRange(NumsPath(dbase), LastPosOffset, 4);
        return read == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
    }

    private void WriteNumsSegments(int dbase, List<(long, ReadOnlyMemory<byte>)> segments)
    {
        if (dbase is not (0 or 1)) throw new IOException($"No such amounts database: {dbase}");
        var path = NumsPath(dbase);
        if (!File.Exists(Resolve(path)))
            segments.Insert(0, (0, NumsMagic));
        WriteSegments(path, segments);
    }

    private static byte[] Int32Bytes(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}
