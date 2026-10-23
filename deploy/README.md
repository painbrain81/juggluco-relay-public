# juggluco-relay: installing on a Linux server

The relay runs as a systemd service on any Linux x64 server the phones can reach. By default it
listens on port **8796**, so it can run next to the official Juggluco server (port 8795) while you
move the phones over.

## 1. Build the package (on your PC)

```powershell
powershell -ExecutionPolicy Bypass -File deploy\package.ps1
scp -r "publish\package" <user>@<server>:~/juggluco-relay
```

Without Windows, publish as shown in the main [README](../README.md) and copy the binary together
with `install.sh`, `juggluco-relay.service`, `appsettings.server.json`, `compare.sh` and
`journal.py` (they need Unix line endings).

## 2. Install (on the server)

```bash
cd ~/juggluco-relay
bash install.sh
sudo nano /opt/juggluco-relay/appsettings.json     # labels and passwords of your phones
sudo systemctl start juggluco-relay
journalctl -u juggluco-relay -f
```

The log should show `Listening on port 8796; hosts: sensor (Source), follower1 (Receiver), follower2 (Receiver)`.

Open the port in the server's firewall (for example `sudo ufw allow 8796/tcp`).

The service runs as the `juggluco` user (created by `install.sh` if missing), writes only to
`/var/lib/juggluco-relay` and restarts by itself if it stops. To update, repeat steps 1 and 2:
`install.sh` never touches the configuration or the data, and keeps the previous binary for
rolling back (it prints the command).

## 3. Connect the phones

In Juggluco's mirror settings, on each phone, add a connection to the server's address and
port 8796, with the same label and password as its entry in `appsettings.json`: on the sensor
phone one that sends amounts, scans and stream, on the followers one that receives.

When the sensor phone connects for the first time the log shows:

```
sensor: connected from ... (first contact: asking for a full resend)
```

and the phone sends its whole history to the relay; it can take a few minutes. A follower shows:

```
follower1: connected from ...
follower1: forwarding from position 0 (journal ...)
```

## Migrating from the official Juggluco server

If the phones currently mirror through the official Juggluco command-line server, you can move
them one at a time without downtime:

1. Install the relay on the same server (port 8796 doesn't clash with 8795).
2. On the sensor phone, **add** a connection identical to the existing one (same address, label,
   password and data sent) but with port 8796. Leave the old one in place.
3. After half an hour, compare the two servers:

   ```bash
   sudo bash ~/juggluco-relay/compare.sh /home/juggluco juggluco
   ```

   The arguments are the folder holding the official server's `juggluco` binary and its
   `jugglucodata`, and the account it runs as. Expected: identical `polls.dat` on the common part
   and, with `StreamExport` enabled, an identical `stream.tsv` up to the last value present in both.
4. On one follower, change **only the port** of its connection from 8795 to 8796. Its graph and
   last value must stay the same as on followers still on the official server. Keep it like
   this for a few days.
5. Move the remaining followers, then remove the old connection from the sensor phone and stop
   the official server.

## Stream export

With `StreamExport.Enabled`, the relay writes a `stream.tsv` in the same format as
`juggluco -B`, a few seconds after new data arrives and anyway every `RefreshSeconds`. A consumer
can use the file's modification time to tell a stalled relay from a phone that is simply not
sending. If `StreamExport.Path` is outside `/var/lib/juggluco-relay`, add its folder to
`ReadWritePaths` in `juggluco-relay.service`, or the sandbox will block the write.

## Inspecting the journal

```bash
sudo python3 ~/juggluco-relay/journal.py <position> [count]
```

lists journal entries from a position onwards (read only), with command name, length, ack flag
and CRC check. The position of each receiver is in `state.json`.

## Files on the server

| Path | What |
|---|---|
| `/opt/juggluco-relay/JugglucoRelay` | the program (a single file) |
| `/opt/juggluco-relay/appsettings.json` | configuration, with the passwords (mode 640) |
| `/var/lib/juggluco-relay/files/` | copy of the source phone's files, same layout as `jugglucodata` |
| `/var/lib/juggluco-relay/journal.bin` | journal of the commands to forward |
| `/var/lib/juggluco-relay/state.json` | per connection: last contact and position in the journal |
| `/var/lib/juggluco-relay/stream.tsv` | optional stream export, same format as `juggluco -B` |
