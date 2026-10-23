using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JugglucoRelay.Crypto;
using JugglucoRelay.Export;
using JugglucoRelay.Protocol;
using JugglucoRelay.Relay;
using JugglucoRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JugglucoRelay.Tests;

/// <summary>
/// Real relay on a local port, with two fake phones speaking the protocol like
/// the app (the "connector" side of docs/protocol/spec-1-connection.md §1.2).
/// </summary>
public sealed class RelayEndToEndTests : IAsyncLifetime
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "jrelay-" + Guid.NewGuid().ToString("N"));
    private readonly int port = FreePort();
    private IHost host = null!;

    public async Task InitializeAsync()
    {
        var options = new RelayOptions
        {
            Port = port,
            DataDirectory = dir,
            HandshakeTimeoutSeconds = 5,
            AckTimeoutSeconds = 5,
            Hosts =
            [
                new() { Name = "sensor", Label = "sensor", Password = "secret1", Role = HostRole.Source },
                new() { Name = "follower1", Label = "follower1", Password = "secret2", Role = HostRole.Receiver },
            ],
            StreamExport = new() { Enabled = true, Path = Path.Combine(dir, "stream.tsv"), DebounceSeconds = 0 },
        };
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(new FileStore(Path.Combine(dir, "files")));
        services.AddSingleton(new Journal(Path.Combine(dir, "journal.bin")));
        services.AddSingleton(new HostStateStore(Path.Combine(dir, "state.json")));
        services.AddSingleton<StreamExporter>();
        services.AddHostedService(sp => sp.GetRequiredService<StreamExporter>());
        services.AddHostedService<RelayServer>();
        host = new HostBuilder().ConfigureServices(s => { foreach (var d in services) s.Add(d); }).Build();
        await host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SourceData_IsStored_Answered_AndReplayedToReceiver()
    {
        var sent = new List<byte[]>();
        var polls = Scan(1792039972, 7667, 106, 3, 0.45f).Concat(Scan(1792040032, 7668, 107, 4, 1.2f)).ToArray();

        using (var phone = await Phone.ConnectAsync(port, "sensor", "secret1", isNew: false))
        {
            Assert.Equal(0, phone.ReplyLastByte); // first contact: the relay asks for a full resend

            var cmd = FileOnce("sensors/E07A-XX0T039DKA9/polls.dat", 0, [(0, polls)]);
            sent.Add(cmd);
            await phone.SendAsync(cmd, ack: true);

            var info = FileOnce("sensors/E07A-XX0T039DKA9/info.dat", 0x8000 | 0, [(76, BitConverter.GetBytes(2))], extra: BitConverter.GetBytes(0));
            sent.Add(info);
            await phone.SendAsync(info, ack: true);

            var nums = Nums(dbase: 0, last: 2, index: 0, records: new byte[32]);
            sent.Add(nums);
            await phone.SendAsync(nums, ack: true);

            byte[] render = [8, 0, 1, 0];
            sent.Add(render);
            await phone.SendAsync(render, ack: true);

            // Queries from the phone: encrypted replies in the opposite direction, same Ascon state
            await phone.SendAsync([14, 0, 0, 0], ack: false);
            var last = await phone.Channel.ReadFrameAsync(default);
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(last));

            var ask = AskFile("sensors/E07A-XX0T039DKA9/polls.dat", 20, 20);
            await phone.SendAsync(ask, ack: false);
            var file = await phone.Channel.ReadFrameAsync(default);
            Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(file));
            Assert.Equal(polls.AsSpan(20, 20).ToArray(), file.AsSpan(4, 20).ToArray());

            // After the queries the phone resumes sending: another change of direction
            byte[] render2 = [8, 0, 8, 0];
            sent.Add(render2);
            await phone.SendAsync(render2, ack: true);
        }

        Assert.Equal(polls, File.ReadAllBytes(Path.Combine(dir, "files/sensors/E07A-XX0T039DKA9/polls.dat")));

        using (var receiver = await Phone.ConnectAsync(port, "follower1", "secret2", isNew: true))
        {
            Assert.Equal(1, receiver.ReplyLastByte);
            foreach (var expected in sent)
                Assert.Equal(expected, await receiver.ReceiveAsync());
            Assert.Equal(new byte[] { 16, 0, 0, 0 }, await receiver.ReceiveAsync()); // final suptodate
        }

        var tsv = await WaitForAsync(Path.Combine(dir, "stream.tsv"));
        var lines = tsv.TrimEnd('\n').Split('\n');
        Assert.Equal("Sensorid\tnr\tUnixTime\tYYYY-mm-ddTHH:MM:SS\tTZ\tMin\tmmol/L\tRate\tChangeLabel", lines[0]);
        Assert.Equal(3, lines.Length);
        var cols = lines[1].Split('\t');
        Assert.Equal(["XX0T039DKA9", "1", "1792039972"], cols[..3]);
        Assert.Equal(["7667", "5.9", "+0.45", "STABLE"], cols[5..]);
        Assert.Equal(["7668", "5.9", "+1.2", "RISING"], lines[2].Split('\t')[5..]);
    }

    [Fact]
    public async Task FirstContact_PhoneResetsAndCloses_NextAttemptProceeds()
    {
        // Behaviour seen with the real app: on receiving the 0 it resets its state and closes at once,
        // without sending the nonce. On the next attempt the relay must answer 1.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var s = tcp.GetStream();
            await s.WriteAsync(RelayServer.LabelBytes("sensor"));
            await s.WriteAsync(SendMagic(isNew: false));
            var reply = new byte[15];
            await s.ReadExactlyAsync(reply);
            Assert.Equal(0, reply[14]);
        }
        using var phone = await Phone.ConnectAsync(port, "sensor", "secret1", isNew: false);
        Assert.Equal(1, phone.ReplyLastByte);
        await phone.SendAsync([8, 0, 1, 0], ack: true);
    }

    [Fact]
    public async Task ReceiverProbe_IsAnswered_WithoutSendingData()
    {
        // The app's probeMirrorTcp: encrypted frame with just a sack, waits for the ack and closes
        using var phone = await Phone.ConnectAsync(port, "follower1", "secret2", isNew: true);
        await phone.SendAsync(Wire.AckRequest.ToArray(), ack: false);
        await phone.Channel.ReadAckAsync(new CancellationTokenSource(5000).Token);
        await AssertClosedAsync(phone.Stream);
    }

    [Fact]
    public async Task UnknownLabel_IsRejected()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(RelayServer.LabelBytes("stranger"));
        await s.WriteAsync(SendMagic(isNew: false));
        await AssertClosedAsync(s);
    }

    [Fact]
    public async Task WrongPassword_ClosesConnection()
    {
        using var phone = await Phone.ConnectAsync(port, "sensor", "wrong", isNew: false);
        await phone.Channel.WriteFrameAsync(new byte[] { 8, 0, 1, 0 }, askAck: true, default);
        // With the wrong key the relay reads a random frame length and may wait for bytes that never
        // come: closing our side makes it reach either the tag check or the end of the stream.
        phone.Stream.Socket.Shutdown(SocketShutdown.Send);
        await AssertClosedAsync(phone.Stream).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PathTraversal_ClosesConnection()
    {
        using var phone = await Phone.ConnectAsync(port, "sensor", "secret1", isNew: false);
        await Assert.ThrowsAnyAsync<IOException>(() => phone.SendAsync(FileOnce("../outside.dat", 0, [(0, [1, 2, 3, 4])]), ack: true));
        Assert.False(File.Exists(Path.Combine(dir, "outside.dat")));
    }

    /// <summary>The relay closes without answering: end of stream or reset, depending on the OS.</summary>
    private static async Task AssertClosedAsync(NetworkStream s)
    {
        try { Assert.Equal(0, await s.ReadAsync(new byte[15])); }
        catch (IOException) { }
    }

    // --- fake phone

    private sealed class Phone : IDisposable
    {
        private readonly TcpClient tcp;
        public NetworkStream Stream { get; }
        public FrameChannel Channel { get; private set; } = null!;
        public int ReplyLastByte { get; private set; }

        private Phone(TcpClient tcp) { this.tcp = tcp; Stream = tcp.GetStream(); }

        public static async Task<Phone> ConnectAsync(int port, string label, string password, bool isNew)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var p = new Phone(tcp);
            await p.Stream.WriteAsync(RelayServer.LabelBytes(label));
            await p.Stream.WriteAsync(SendMagic(isNew));
            var reply = new byte[15];
            await p.Stream.ReadExactlyAsync(reply);
            Assert.Equal(Wire.ReceiveMagicPrefix.ToArray(), reply[..14]);
            p.ReplyLastByte = reply[14];
            var nonce = new byte[16];
            Random.Shared.NextBytes(nonce.AsSpan(0, 8));
            await p.Stream.WriteAsync(nonce.AsMemory(0, 8));
            await p.Stream.ReadExactlyAsync(nonce.AsMemory(8));
            p.Channel = new FrameChannel(p.Stream, new AsconSession(JugglucoKey.FromPassword(password)!, nonce));
            return p;
        }

        public async Task SendAsync(byte[] command, bool ack)
        {
            await Channel.WriteFrameAsync(command, ack, default);
            if (ack) await Channel.ReadAckAsync(new CancellationTokenSource(5000).Token);
        }

        /// <summary>Receives a forwarded command and, if it asks for an ack, sends it.</summary>
        public async Task<byte[]> ReceiveAsync()
        {
            var frame = await Channel.ReadFrameAsync(new CancellationTokenSource(5000).Token);
            if (frame.Length >= 4 && frame.AsSpan(frame.Length - 4).SequenceEqual(Wire.AckRequest))
            {
                await Channel.WriteAckAsync(default);
                frame = frame[..^4];
                // strip the padding up to a multiple of 4 before the sack
                return frame;
            }
            return frame;
        }

        public void Dispose() => tcp.Dispose();
    }

    // --- building the commands the way the app does (Juggluco net/sendcommands.cpp:346-383)

    private static byte[] SendMagic(bool isNew)
    {
        var m = new byte[14];
        Wire.SendMagicPrefix.CopyTo(m);
        m[10] = 1; m[11] = 2; m[12] = 3; m[13] = isNew ? (byte)0 : (byte)7;
        return m;
    }

    private static byte[] FileOnce(string name, ushort dowith, (int Off, byte[] Data)[] segs, byte[]? extra = null)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        int dataLen = segs.Sum(s => s.Data.Length);
        int body = Wire.Align4(12 + 8 * segs.Length + nameBytes.Length + dataLen);
        int total = body + (extra?.Length ?? 0);
        var b = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 17);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), total);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)segs.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), dowith);
        int pos = 12;
        foreach (var (off, data) in segs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pos), off);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pos + 4), data.Length);
            pos += 8;
        }
        nameBytes.CopyTo(b, pos);
        pos += nameBytes.Length;
        foreach (var (_, data) in segs) { data.CopyTo(b, pos); pos += data.Length; }
        extra?.CopyTo(b, body);
        return b;
    }

    private static byte[] Nums(byte dbase, uint last, uint index, byte[] records)
    {
        var b = new byte[16 + 8 + records.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 10);
        b[2] = dbase; b[3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), last);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), index);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), records.Length / 16);
        records.CopyTo(b, 24);
        return b;
    }

    private static byte[] AskFile(string name, uint off, uint len)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var b = new byte[Wire.Align4(12 + nameBytes.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 15);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), off);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), len);
        nameBytes.CopyTo(b, 12);
        return b;
    }

    private static byte[] Scan(uint t, int id, int g, int tr, float ch)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(b, t);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), g);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), tr);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), ch);
        return b;
    }

    private static async Task<string> WaitForAsync(string path)
    {
        for (int i = 0; i < 50 && !File.Exists(path); i++) await Task.Delay(100);
        await Task.Delay(300);
        return await File.ReadAllTextAsync(path);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
