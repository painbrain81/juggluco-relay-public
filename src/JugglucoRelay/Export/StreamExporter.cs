using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JugglucoRelay.Export;

/// <summary>
/// Writes the sensors' stream in the same format as <c>juggluco -B</c>
/// (Juggluco export/exports.cpp: currentheader and fexportscans), for external consumers.
/// The file is rewritten atomically shortly after new data arrives, instead of running a
/// second juggluco process on the same directory every minute, and in any case every
/// RefreshSeconds even if nothing arrives.
/// </summary>
public sealed class StreamExporter(IOptions<RelayOptions> options, FileStore files, ILogger<StreamExporter> logger) : BackgroundService
{
    // ScanData (Juggluco SensorGlucoseData.hpp:142): uint32 t, int32 id, int32 g (mg/dL), int32 tr, float ch
    private const int ScanSize = 20;
    private const int MaxMinutes = 46 * 24 * 60;
    // Juggluco defines CONV18 (settings/settings.hpp:39): divides by exactly 18, not by 18.0182
    private const double MmolFactor = 1.0 / 180.0;

    private static readonly string[] Trends = ["NOT_DETERMINED", "FALLING_QUICKLY", "FALLING", "STABLE", "RISING", "RISING_QUICKLY"];

    private readonly StreamExportOptions config = options.Value.StreamExport;
    private readonly SemaphoreSlim requested = new(0, 1);

    private readonly record struct Row(string Sensor, int Order, int Nr, uint Time, int Id, int Glucose, int Trend, float Rate);

    /// <summary>Requests a new export (several requests close together become one).</summary>
    public void Request()
    {
        if (!config.Enabled) return;
        try { requested.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Enabled) return;
        Request();
        var refresh = config.RefreshSeconds > 0 ? TimeSpan.FromSeconds(config.RefreshSeconds) : Timeout.InfiniteTimeSpan;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Without requests within refresh, rewrite anyway, like a periodic juggluco -B would
            if (await requested.WaitAsync(refresh, stoppingToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(config.DebounceSeconds), stoppingToken);
                while (requested.CurrentCount > 0) await requested.WaitAsync(stoppingToken);
            }
            try
            {
                int rows = Export(Path.GetFullPath(config.Path, AppContext.BaseDirectory));
                logger.LogDebug("stream.tsv updated: {Rows} rows", rows);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Export of {Path} failed: {Message}", config.Path, ex.Message);
            }
        }
    }

    /// <summary>Writes the file and returns the number of data rows.</summary>
    public int Export(string path)
    {
        var rows = ReadRows();
        var sb = new StringBuilder(rows.Count * 80);
        sb.Append("Sensorid\tnr\tUnixTime\tYYYY-mm-ddTHH:MM:SS\tTZ\tMin\t").Append(config.Unit).Append("\tRate\tChangeLabel\n");
        bool mmol = config.Unit.StartsWith("mmol", StringComparison.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(r.Time).ToLocalTime();
            sb.Append(r.Sensor).Append('\t')
              .Append(r.Nr.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Time.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append('\t')
              .Append(FormatG((float)(local.Offset.TotalSeconds / 3600.0), sign: false)).Append('\t')
              .Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(mmol
                  ? ((double)(float)(r.Glucose * 10 * MmolFactor)).ToString("F1", CultureInfo.InvariantCulture)
                  : ((double)(float)(r.Glucose * 10 * 0.1)).ToString("F0", CultureInfo.InvariantCulture)).Append('\t')
              .Append(FormatG(r.Rate, sign: true)).Append('\t')
              .Append(r.Trend >= 0 && r.Trend < Trends.Length ? Trends[r.Trend] : Trends[0]).Append('\n');
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            fs.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(tmp, path, overwrite: true);
        return rows.Count;
    }

    /// <summary>Stream values of all sensors, filtered like ScanData::valid() and in time order.</summary>
    private List<Row> ReadRows()
    {
        var rows = new List<Row>();
        var sensorsDir = Path.Combine(files.Root, "sensors");
        if (!Directory.Exists(sensorsDir)) return rows;

        var perSensor = new List<List<Row>>();
        foreach (var dir in Directory.EnumerateDirectories(sensorsDir))
        {
            var polls = Path.Combine(dir, "polls.dat");
            if (!File.Exists(polls)) continue;
            var dirName = Path.GetFileName(dir);
            // shortsensorname(): the last 11 characters of the directory name
            var sensor = dirName.Length > 11 ? dirName[^11..] : dirName;
            var data = File.ReadAllBytes(polls);
            var list = new List<Row>();
            int prevId = int.MinValue;
            for (int pos = 0; pos + ScanSize <= data.Length; pos += ScanSize)
            {
                var s = data.AsSpan(pos, ScanSize);
                uint t = BinaryPrimitives.ReadUInt32LittleEndian(s);
                int id = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
                int g = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
                int tr = BinaryPrimitives.ReadInt32LittleEndian(s[12..]);
                float ch = BinaryPrimitives.ReadSingleLittleEndian(s[16..]);
                bool duplicate = pos > 0 && id == prevId;
                prevId = id;
                bool valid = t != 0 && g != 0 && g < 552 && id >= 0 && id < MaxMinutes && t > 1598911200u && t < 2145909600u;
                if (valid && !duplicate)
                    list.Add(new Row(sensor, 0, pos / ScanSize + 1, t, id, g, tr, ch));
            }
            if (list.Count > 0) perSensor.Add(list);
        }

        // Juggluco merges the sensors starting from the oldest, each time taking the least recent value
        perSensor.Sort((a, b) => a[0].Time.CompareTo(b[0].Time));
        for (int i = 0; i < perSensor.Count; i++)
            foreach (var r in perSensor[i]) rows.Add(r with { Order = i });
        rows.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time)
            : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Nr.CompareTo(b.Nr));
        return rows;
    }

    /// <summary>Like libc printf "%g" / "%+g".</summary>
    public static string FormatG(float value, bool sign)
    {
        if (float.IsNaN(value)) return (float.IsNegative(value) ? "-" : sign ? "+" : "") + "nan";
        if (float.IsInfinity(value)) return (value < 0 ? "-" : sign ? "+" : "") + "inf";
        var s = ((double)value).ToString("G6", CultureInfo.InvariantCulture).Replace('E', 'e');
        int e = s.IndexOf('e');
        if (e >= 0 && s.Length - e == 3) s = s[..(e + 2)] + "0" + s[(e + 2)..];
        if (sign && !s.StartsWith('-')) s = "+" + s;
        if (value == 0 && float.IsNegative(value) && !s.StartsWith('-')) s = "-" + s.TrimStart('+');
        return s;
    }
}
