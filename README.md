# juggluco-relay

A small relay server for the [Juggluco](https://github.com/j-kaltes/Juggluco) mirror protocol.
One phone, the one paired with the sensor, sends its data to the relay; the relay stores it and
forwards it to any number of other Juggluco phones. Followers see exactly what the sensor phone
sees, from anywhere, without the phones having to reach each other.

```
                               ┌──────────────────────┐
  sensor phone  ── Juggluco ──▶│    juggluco-relay    │── Juggluco ──▶  follower 1
  (Source)        mirror       │  files + journal on  │   mirror      (Receiver)
                               │  disk, one position  │── Juggluco ──▶  follower 2
                               │  per receiver        │               (Receiver)
                               └──────────────────────┘
```

> [!WARNING]
> **Not a medical device.** This is a personal hobby project, not affiliated with or endorsed by
> the author of Juggluco or by any sensor manufacturer. Data may arrive late, incomplete or not at
> all. Do not use it to make treatment decisions, and do not rely on it for alarms. It comes with
> no warranty of any kind (see [LICENSE](LICENSE)).

## Why

Juggluco can already mirror through its own command-line server. The relay does one thing only,
forwarding, and is built around that:

- **Forward only.** It speaks the mirror protocol and nothing else: no rendering, no web server,
  no Nightscout API. One self-contained binary (no .NET runtime needed on the server), under
  100 MB of RAM in normal use.
- **A slow phone doesn't block the others.** Every connection has its own task and a short
  handshake timeout, instead of handshaking on the accept thread.
- **Same stream, same result.** Every command received from the source phone is appended to a
  journal; each receiver has its own position in it and replays from there, so it ends up in the
  same state as the source.
- **Acks mean "on disk".** The source phone is acknowledged only after the data and the journal
  entry are written, so it never has to resend data the relay already confirmed.
- **Encrypted connections only.** Every host must have a password, so the Ascon-128a encryption
  of the mirror protocol is always on.
- **Linux service ready.** systemd unit with sandboxing and a memory cap, an install script that
  never touches configuration or data, and a one-command rollback.

Optionally it also writes a `stream.tsv` file in the same format as `juggluco -B`, for anything
that wants the readings without speaking the protocol.

## Compatibility

The protocol was reverse-engineered from the Juggluco 11.0.x source
([commit `43fdbe1`](https://github.com/j-kaltes/Juggluco/tree/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp)).
Source references in the code and docs, such as `datbackup.hpp:667-681`, point to that commit,
relative to `Common/src/main/cpp`. The phones run the regular Juggluco Android app.

## Quick start

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; a Linux x64
server with systemd to run it, reachable by the phones on one TCP port (default **8796**).

```bash
dotnet test                                    # build and run the tests
dotnet publish src/JugglucoRelay -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -o publish/linux-x64
```

On Windows, `deploy\package.ps1` does the publish and collects everything the server needs in
`publish\package`. Installation, updates, rollback and migration from the official Juggluco server
are described in [deploy/README.md](deploy/README.md).

## Configuration

`appsettings.json`, section `Relay` (a server template is in
[deploy/appsettings.server.json](deploy/appsettings.server.json)):

| Key | Default | Meaning |
|---|---|---|
| `Port` | `8796` | TCP port the phones connect to |
| `DataDirectory` | `data` | received files, journal and per-connection state |
| `HandshakeTimeoutSeconds` | `30` | a connection that hasn't finished the handshake by then is closed |
| `AckTimeoutSeconds` | `60` | how long to wait for a receiving phone's ack |
| `Hosts` | | one entry per phone, see below |
| `StreamExport.Enabled` | `false` | write `stream.tsv` |
| `StreamExport.Path` | `/var/lib/juggluco-relay/stream.tsv` | where to write it |
| `StreamExport.Unit` | `mmol/L` | `mmol/L` or `mg/dL` |
| `StreamExport.DebounceSeconds` | `5` | wait after the last received data before rewriting the file |
| `StreamExport.RefreshSeconds` | `60` | rewrite anyway this often, so the file's modification time shows the relay is alive (0 = only on new data) |

Each host:

| Key | Meaning |
|---|---|
| `Name` | name used in logs and in the state file |
| `Label` | connection label, identical to the one set in Juggluco on that phone (max 16 bytes) |
| `Password` | connection password, identical to the one in Juggluco (required) |
| `Role` | `Source` for the phone with the sensor, `Receiver` for the followers |
| `Disabled` | `true` to ignore the entry without deleting it |

At least one `Source` is required; names and labels must be unique.

## Setting up the phones

The relay behaves like the official Juggluco server on the mirror port, so the phones are set up
the same way, in Juggluco's mirror settings, with the relay's address and port and the same label
and password as in `appsettings.json`:

- **sensor phone**: a connection that sends amounts, scans and stream to the relay;
- **each follower**: a connection that receives from the relay.

The first time the sensor phone connects, the relay asks it for a full resend, so the history
arrives too. A follower replays the journal from the start the first time it connects, or
whenever the phone asks to start over; after that it picks up from its last acknowledged
position.

## Documentation

- [docs/protocol/](docs/protocol/): the Juggluco mirror protocol as implemented here:
  [connection, framing and encryption](docs/protocol/spec-1-connection.md),
  [receiving](docs/protocol/spec-2-receive.md), [sending](docs/protocol/spec-3-send.md),
  plus `jascon.py`, a Python transliteration of Juggluco's modified LibAscon used to produce the
  reference vectors.
- [deploy/README.md](deploy/README.md): installing and running on a Linux server.

## Credits

[Juggluco](https://github.com/j-kaltes/Juggluco) is by Jaap Korthals Altes and is licensed under
GPL-3.0. This project reimplements its mirror protocol in C#, written from Juggluco's source
(including its modified LibAscon, by Matjaž Guštin), and is released under the same license.

## License

[GPL-3.0](LICENSE)
