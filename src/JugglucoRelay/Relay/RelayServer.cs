using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using JugglucoRelay.Crypto;
using JugglucoRelay.Export;
using JugglucoRelay.Protocol;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JugglucoRelay.Relay;

/// <summary>
/// Accepts the phones' connections on the mirror port. Unlike Juggluco, which did the
/// handshake on the accept thread (one slow phone blocked everyone), each connection
/// has its own task and a short handshake timeout.
/// </summary>
public sealed class RelayServer(
    IOptions<RelayOptions> options,
    FileStore files,
    Journal journal,
    HostStateStore states,
    StreamExporter exporter,
    ILogger<RelayServer> logger) : BackgroundService
{
    private sealed record Host(HostOptions Options, byte[] Label, byte[] Key);

    private readonly RelayOptions config = options.Value;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, ConnectionProgress Progress)> active = new();
    private List<Host> hosts = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hosts = config.Hosts.Where(h => !h.Disabled)
            .Select(h => new Host(h, LabelBytes(h.Label), JugglucoKey.FromPassword(h.Password)!))
            .ToList();

        var listener = new TcpListener(IPAddress.IPv6Any, config.Port);
        listener.Server.DualMode = true;
        listener.Start();
        logger.LogInformation("Listening on port {Port}; hosts: {Hosts}", config.Port,
            string.Join(", ", hosts.Select(h => $"{h.Options.Name} ({h.Options.Role})")));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var progress = new ConnectionProgress(peer);
        Host? host = null;
        CancellationTokenSource? mine = null;
        try
        {
            using var _ = client;
            ConfigureSocket(client.Client);
            var stream = client.GetStream();

            // --- Handshake, with an overall time limit
            FrameChannel channel;
            bool phoneIsNew;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                hs.CancelAfter(TimeSpan.FromSeconds(config.HandshakeTimeoutSeconds));
                (host, channel, phoneIsNew) = await HandshakeAsync(stream, progress, hs.Token);
                if (host is null) return;
            }

            var name = host.Options.Name;
            mine = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            // One connection per host: the new one replaces the old one, as in Juggluco
            var entry = (mine, progress);
            active.AddOrUpdate(name, entry, (_, old) =>
            {
                logger.LogInformation("{Host}: new connection from {Peer}, closing the previous one ({Old})", name, peer, old.Progress);
                try { old.Cts.Cancel(); } catch (ObjectDisposedException) { }
                return entry;
            });
            using var registration = mine.Token.Register(() => client.Close());

            if (host.Options.Role == HostRole.Source)
                await new SourceSession(name, channel, files, journal, states, exporter.Request, progress, logger).RunAsync(mine.Token);
            else
                await new ReceiverSession(name, channel, journal, states, TimeSpan.FromSeconds(config.AckTimeoutSeconds), progress, logger)
                    .RunAsync(phoneIsNew, mine.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || mine?.IsCancellationRequested == true)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogWarning("{Host} ({Progress}): connection ended: {Message}", progress.Host ?? "unknown", progress, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Host} ({Progress}): unexpected error", progress.Host ?? "unknown", progress);
        }
        finally
        {
            if (host is not null && mine is not null)
                active.TryRemove(new KeyValuePair<string, (CancellationTokenSource, ConnectionProgress)>(host.Options.Name, (mine, progress)));
            mine?.Dispose();
        }
    }

    /// <summary>docs/protocol/spec-1-connection.md §1.2–1.4 and §3.2.</summary>
    private async Task<(Host?, FrameChannel, bool)> HandshakeAsync(NetworkStream stream, ConnectionProgress progress, CancellationToken ct)
    {
        // Every host has a label: the phone always sends it first, followed by the magic.
        var head = new byte[Wire.LabelLength + Wire.SendMagicLength];
        int got = 0;
        try
        {
            while (got < head.Length)
            {
                int n = await stream.ReadAsync(head.AsMemory(got), ct);
                if (n == 0) break;
                got += n;
            }
        }
        catch (OperationCanceledException) { } // handshake timeout: log what has arrived
        if (got < head.Length)
        {
            // Diagnostics: what the phone sent before closing or stalling
            bool magicFirst = got >= Wire.SendMagicPrefix.Length && head.AsSpan(0, Wire.SendMagicPrefix.Length).SequenceEqual(Wire.SendMagicPrefix);
            logger.LogWarning("{Peer}: incomplete handshake after {Seconds:F1} s, {Got} bytes received{Hint}: {Hex}", progress.Peer, progress.Seconds, got,
                magicFirst ? " (the phone does not send the label)" : "", Convert.ToHexString(head, 0, got));
            return (null, null!, false);
        }

        var label = head.AsSpan(0, Wire.LabelLength).ToArray();
        var host = hosts.FirstOrDefault(h => h.Label.AsSpan().SequenceEqual(label));
        if (host is null)
        {
            logger.LogWarning("{Peer}: unknown label \"{Label}\" ({Hex}), connection closed", progress.Peer, Printable(label), Convert.ToHexString(head));
            return (null, null!, false);
        }
        progress.Host = host.Options.Name;

        var magic = head.AsSpan(Wire.LabelLength).ToArray();
        if (!magic.AsSpan(0, Wire.SendMagicPrefix.Length).SequenceEqual(Wire.SendMagicPrefix))
        {
            logger.LogWarning("{Host} ({Peer}): invalid handshake {Magic}", host.Options.Name, progress.Peer, Convert.ToHexString(magic));
            return (null, null!, false);
        }
        bool phoneIsNew = magic[13] == 0;

        // Last byte 0 = "new connection": the source phone resends everything from scratch.
        bool firstContact = host.Options.Role == HostRole.Source && !states.Get(host.Options.Name).Initialized;
        var reply = new byte[Wire.ReceiveMagicLength];
        Wire.ReceiveMagicPrefix.CopyTo(reply);
        reply[^1] = firstContact ? (byte)0 : (byte)1;
        await stream.WriteAsync(reply, ct);
        // Must be recorded right away, like Juggluco (net/backup.cpp:331): on receiving the 0 the phone
        // resets its state towards us and closes this connection; next time it must find a 1, or the
        // reset-and-close loop never ends.
        states.Update(host.Options.Name, s => { s.Initialized = true; s.LastConnected = DateTimeOffset.UtcNow; });

        progress.Stage = "waiting for the nonce after the magic reply";
        var nonce = new byte[16];
        await stream.ReadExactlyAsync(nonce.AsMemory(0, Wire.NonceHalfLength), ct);
        RandomNumberGenerator.Fill(nonce.AsSpan(Wire.NonceHalfLength));
        await stream.WriteAsync(nonce.AsMemory(Wire.NonceHalfLength), ct);
        progress.Stage = "connected";
        logger.LogInformation("{Host}: connected from {Peer}{First}{New}", host.Options.Name, progress.Peer,
            firstContact ? " (first contact: asking for a full resend)" : "",
            phoneIsNew ? " (the phone asks to restart from zero)" : "");
        return (host, new FrameChannel(stream, new AsconSession(host.Key, nonce)), phoneIsNew);
    }

    private static void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        // Like Juggluco (net/backup.cpp:354-408): a vanished phone is detected within a couple of minutes
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 50);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 45);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 2);
    }

    /// <summary>Label as the phone sends it: 16 bytes, truncated or padded with zeros.</summary>
    public static byte[] LabelBytes(string label)
    {
        var bytes = new byte[Wire.LabelLength];
        var raw = Encoding.UTF8.GetBytes(label);
        raw.AsSpan(0, Math.Min(raw.Length, Wire.LabelLength)).CopyTo(bytes);
        return bytes;
    }

    private static string Printable(byte[] label) =>
        new(Encoding.UTF8.GetString(label).TrimEnd('\0').Select(c => char.IsControl(c) ? '?' : c).ToArray());
}
