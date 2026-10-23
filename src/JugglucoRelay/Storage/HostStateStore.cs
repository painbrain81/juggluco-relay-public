using System.Text.Json;

namespace JugglucoRelay.Storage;

public sealed class HostState
{
    /// <summary>
    /// False until the connection has ever completed the handshake. On a source phone's first
    /// handshake the relay answers "new connection", and the phone resends everything from scratch.
    /// </summary>
    public bool Initialized { get; set; }
    /// <summary>For receivers: journal position up to which the phone has acked.</summary>
    public long Cursor { get; set; }
    public DateTimeOffset? LastConnected { get; set; }
    public DateTimeOffset? LastData { get; set; }
}

/// <summary>Connection state in a JSON file, rewritten atomically.</summary>
public sealed class HostStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string path;
    private readonly Lock gate = new();
    private readonly Dictionary<string, HostState> states;

    public HostStateStore(string path)
    {
        this.path = path;
        states = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, HostState>>(File.ReadAllText(path)) ?? []
            : [];
    }

    /// <summary>Copy of a host's current state.</summary>
    public HostState Get(string host)
    {
        lock (gate)
        {
            var s = states.GetValueOrDefault(host) ?? new HostState();
            return new HostState { Initialized = s.Initialized, Cursor = s.Cursor, LastConnected = s.LastConnected, LastData = s.LastData };
        }
    }

    public void Update(string host, Action<HostState> change)
    {
        lock (gate)
        {
            if (!states.TryGetValue(host, out var s))
                states[host] = s = new HostState();
            change(s);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                JsonSerializer.Serialize(fs, states, Json);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}
