namespace JugglucoRelay;

public enum HostRole
{
    /// <summary>Phone that sends the data to the relay (the phone with the sensor).</summary>
    Source,
    /// <summary>Phone that connects to the relay to receive the data.</summary>
    Receiver,
}

public sealed class HostOptions
{
    /// <summary>Name for logs and on-disk state.</summary>
    public string Name { get; set; } = "";
    /// <summary>Connection label, the same as the one set in the app (max 16 characters).</summary>
    public string Label { get; set; } = "";
    public string Password { get; set; } = "";
    public HostRole Role { get; set; }
    public bool Disabled { get; set; }
}

public sealed class StreamExportOptions
{
    public bool Enabled { get; set; }
    /// <summary>Output file, in the same format as <c>juggluco -B</c>.</summary>
    public string Path { get; set; } = "/var/lib/juggluco-relay/stream.tsv";
    /// <summary>"mmol/L" or "mg/dL".</summary>
    public string Unit { get; set; } = "mmol/L";
    /// <summary>Delay after the last data received before rewriting the file.</summary>
    public int DebounceSeconds { get; set; } = 5;
    /// <summary>
    /// Rewrite anyway, even without new data (0 = only when data arrives). A consumer can use the
    /// file's modification time to tell a stalled export from a silent phone.
    /// </summary>
    public int RefreshSeconds { get; set; } = 60;
}

public sealed class RelayOptions
{
    public const string Section = "Relay";

    public int Port { get; set; } = 8796;
    /// <summary>Data directory: received files, journal and connection state.</summary>
    public string DataDirectory { get; set; } = "data";
    /// <summary>Maximum time for the handshake: beyond it, the connection is closed.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = 30;
    /// <summary>Maximum wait for an ack from a receiving phone.</summary>
    public int AckTimeoutSeconds { get; set; } = 60;
    public List<HostOptions> Hosts { get; set; } = [];
    public StreamExportOptions StreamExport { get; set; } = new();
}
