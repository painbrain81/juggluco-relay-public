using System.Diagnostics;

namespace JugglucoRelay.Relay;

/// <summary>
/// Where a connection stands, for the logs: when it drops we know which stage it was in and how
/// long it had been open, and the one replacing it knows what the previous one was doing.
/// </summary>
public sealed class ConnectionProgress(string peer)
{
    private readonly long started = Stopwatch.GetTimestamp();

    public string Peer { get; } = peer;
    /// <summary>Host name, known once the label has been read.</summary>
    public string? Host { get; set; }
    public string Stage { get; set; } = "waiting for the label";

    public double Seconds => Stopwatch.GetElapsedTime(started).TotalSeconds;

    public override string ToString() => $"{Peer}, open for {Seconds:F1} s, {Stage}";
}
