using JugglucoRelay.Protocol;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Logging;

namespace JugglucoRelay.Relay;

/// <summary>
/// Forwards the journal's commands to a receiving phone, from its journal position onwards, then
/// waits for new ones. The saved position advances only after an ack from the phone:
/// if the connection drops, unacked commands are resent on reconnection
/// (writes are by offset, so repeating them does not change the result).
/// </summary>
public sealed class ReceiverSession(
    string hostName,
    FrameChannel channel,
    Journal journal,
    HostStateStore states,
    TimeSpan ackTimeout,
    ConnectionProgress progress,
    ILogger logger)
{
    private static readonly byte[] UpToDate = [16, 0, 0, 0];

    public async Task RunAsync(bool restartFromZero, CancellationToken ct)
    {
        progress.Stage = "waiting for a possible probe";
        if (await AnswerProbeAsync(ct)) return;

        long confirmed = restartFromZero ? 0 : states.Get(hostName).Cursor;
        if (confirmed > journal.End)
        {
            logger.LogWarning("{Host}: position {Cursor} beyond the end of the journal, restarting from zero", hostName, confirmed);
            confirmed = 0;
        }
        if (restartFromZero) states.Update(hostName, s => s.Cursor = 0);
        logger.LogInformation("{Host}: forwarding from position {Cursor} (journal {End})", hostName, confirmed, journal.End);

        long position = confirmed;
        while (!ct.IsCancellationRequested)
        {
            int sent = 0;
            foreach (var entry in journal.ReadFrom(position))
            {
                progress.Stage = $"sending position {entry.Offset}";
                await channel.WriteFrameAsync(entry.Command, entry.AskAck, ct);
                if (entry.AskAck)
                {
                    progress.Stage = $"waiting for the ack of position {entry.Offset}";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ackTimeout);
                    await channel.ReadAckAsync(timeout.Token);
                    confirmed = entry.Next;
                }
                position = entry.Next;
                // A full resend has thousands of commands: save now and then, not only at the end
                if (++sent % 500 == 0) SaveCursor(confirmed);
            }

            if (sent > 0)
            {
                // Like Juggluco at the end of an update: the phone records the time of the last sync.
                await channel.WriteFrameAsync(UpToDate, askAck: false, ct);
                SaveCursor(confirmed);
                logger.Log(sent >= 20 ? LogLevel.Information : LogLevel.Debug,
                    "{Host}: forwarded {Count} commands, position {Cursor}", hostName, sent, confirmed);
            }
            progress.Stage = $"waiting for data, acked up to {confirmed}";
            await journal.WaitBeyondAsync(position, ct);
        }
    }

    /// <summary>
    /// Before using TCP/IP the app probes the server: it connects, sends an encrypted frame with
    /// just a sack and waits for the ack, then closes (probeMirrorTcp, Juggluco net/sender.cpp:680-710).
    /// A normal receiver connection instead sends nothing and waits for data. If a frame arrives
    /// shortly, it is the probe: answer and close.
    /// </summary>
    private async Task<bool> AnswerProbeAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30 && !channel.DataAvailable; i++)
            await Task.Delay(50, ct);
        if (!channel.DataAvailable) return false;

        var frame = await channel.ReadFrameAsync(ct);
        if (!frame.AsSpan().SequenceEqual(Wire.AckRequest))
            throw new ProtocolException($"The receiving phone sent an unexpected command: {Convert.ToHexString(frame)}");
        await channel.WriteAckAsync(ct);
        logger.LogInformation("{Host}: connection and password probe passed", hostName);
        return true;
    }

    private void SaveCursor(long cursor) =>
        states.Update(hostName, s => { s.Cursor = cursor; s.LastData = DateTimeOffset.UtcNow; });
}
