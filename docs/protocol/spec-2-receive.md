# Juggluco mirror protocol — Part 2: the RECEIVE side (commands from a sending phone)

Scope: what the Juggluco server (the Linux "cmdline" build of the Juggluco sources) does
with every command that a *sending* phone transmits on the mirror connection (TCP 8795 by
default on the phone side), after the handshake/encryption layer (covered by spec-1) has
produced a plaintext command buffer.

Source references point to Juggluco at commit
[43fdbe1](https://github.com/j-kaltes/Juggluco/tree/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp)
(11.0.x); paths are relative to `Common/src/main/cpp`. A bare `:N` refers to the file
named last.

Build facts that matter ([`CMakeLists.txt:389-476`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/CMakeLists.txt#L389-L476)): the server executable is built **without**
`JUGGLUCO_APP` and **without** `WEAROS`; it defines `NOJAVA NOTAPP DONT_USE_LIBREVIEW=1
LIBRE3=1 DEXCOM=1 SIBIONICS=1 USE_MEAL`. So everything inside `#ifdef JUGGLUCO_APP`
(Garmin, `sBlueWatch` handler) is absent on the server, and `render()` /
`javaUpdateDevices()` are no-op stubs ([`cmdline/main.cpp:887`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L887), [`954`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L954)).

---------------------------------------------------------------------------------------

## 0. TL;DR for a reimplementation

* Every command is a little-endian, natively-packed C struct starting with a `uint16_t`
  command code. Commands are concatenated; **after each command the read cursor is rounded
  up to a multiple of 4** ([`net/getcommand.cpp:377`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L377)).
* The workhorse is `sfileonce` (17): "write these N (offset,len) ranges into file
  `<relative path>`", plus an optional `dowith` hint. ~90 % of traffic. It is a pure
  positional write → **can be stored opaquely** (pwrite into `<datadir>/<path>`).
* Nums ("amounts" in Juggluco's UI: manual entries such as insulin, carbs, fingersticks…) arrive as structured commands
  (`snuminit` 13, `snums` 10, `snumnr` 11) — fixed 16-byte records addressed by index in two
  databases (dbase 0 = "here", 1 = "watch"). Also storable opaquely (array of 16-byte blobs
  + `firstpos`/`lastpos` per dbase).
* The phone *asks* the server two things and expects data back: `sasklastnum` (14) → int
  lastpos of a nums dbase; `saskfile` (15) → bytes of a file range (used to read
  `sensors/<name>/info.dat`).
* Acknowledgement: the phone appends an `sack` (12) command after each command it wants
  acknowledged; the server answers with the raw 4 bytes `0x11 0x00 0x0E 0x00`
  (uint32 917521) **unencrypted, even on an encrypted connection** ([`net/getcommand.cpp:161-171`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L161-L171),
  [`comtypes.hpp:129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L129)). Ack = "all previous commands in this stream were applied".
* Anything unknown / failing ⇒ the server drops the connection (no NAK exists).
* Forwarding to other phones is driven by **in-memory/persisted cursors** that the receive
  handlers lower ("resend from here") + a wake-up (`srender`, `sbackup`, `swakeupstream`,
  and `snums` itself). See §5. A reimplementation that only stores and never forwards can
  ignore §5 entirely.

---------------------------------------------------------------------------------------

## 1. Framing of the plaintext command buffer (receive loop)

### 1.1 Interpreter
`Connect::interpret(host, ctx, buf, len)` [`net/getcommand.cpp:137-380`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L137-L380).

```
for (it = 0; it < len; ) {
    if (len - it < 2)  -> "need more" (comlen = 4)                    :142-146
    command = *(uint16_t*)(buf+it)
    if (!(host->receivefrom & 2) && command not in
          {sBlueWatch, sbackupstop, swakeupstream, sbackup, sack})
        -> savemessage("Other side sends, but I am not configured to receive"); CLOSE   :152-157
    switch(command) { ... compute comlen, it += comlen;
                      if (it > len) -> return {it, comlen}  ("incomplete, need more")
                      ret = handler(...) }
    if (!ret) CLOSE (return {-1,0})                                    :375-376
    it = align4(it)                                                    :377
}
return {0,0}
```
* Return `{-1,0}` ⇒ connection is torn down (getcommandsnopass returns false → `shutdownReceiver`,
  [`net/backup.cpp:410-426`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L410-L426); encrypted path: interpretcommands returns 2 → getcom returns false).
* `default:` (unknown code, incl. 6 `srenum`, 9 `snewnums`, 99 `stest`, and 22 `sBlueWatch`
  on the server build) leaves `ret=false` ⇒ CLOSE.

### 1.2 No-password stream (getcommandsnopass, [`net/getcommand.cpp:393-463`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L393-L463))
Plain TCP byte stream, 1 MiB initial buffer. When `interpret` returns `{last,comlen}` with
`last>0` the incomplete command starts at `last-comlen`; it is moved to the buffer start
(buffer grows to `comlen` if larger) and more bytes are read. So commands may be split
arbitrarily across TCP segments.

### 1.3 Password (Ascon) messages (getcom, [`net/getcommand.cpp:482-590`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L482-L590))
Each message = `tag[16]` + ciphertext of `{int32 datlen; uint8 cmds[datlen]; zero-pad to ≥16}`.
`interpretcommands(uit+4, datlen)` ([`:583`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L583), [`:467-479`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L467-L479)). An incomplete command inside one
message is an error ("Strange error last>0") ⇒ CLOSE. So in encrypted mode every message
contains whole commands. A typical message produced by `Connect::sendcommand(pass,…)`
([`net/sendcommands.cpp:217-271`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L217-L271)) is `[command][zero pad to 4][sack 0x000C,0x0001]`.
(Crypto details: spec-1.)

### 1.4 How the phone builds what we receive (for orientation)
* `sendcommand` (ack wanted), [`net/sendcommands.cpp:186-210`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L186-L210) (plain) / [`217-277`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L217-L277) (crypt):
  command bytes, zero padding to 4, then `sendack{uint16 12, uint16 1}`, then waits for the
  4-byte ack (`getack`, [`:171-185`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L171-L185); any other value ⇒ failure).
* `s_noacksendcommand` / `noacksendone`: command only, no ack expected.

---------------------------------------------------------------------------------------

## 2. Command table (codes: [`net/comtypes.hpp:26-48`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L26-L48))

All integers little-endian; struct layout = natural C alignment (no packing unless noted).
"Ack?" = whether the *phone* appends `sack` after it (i.e. waits for 917521).

| Code | Name | Sent by phone via | Ack? | Payload size |
|---|---|---|---|---|
| 1 | sopen | (legacy, sender code commented out [`sendcommands.cpp:52-91`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L52-L91)) | — | 8+len+1 |
| 2 | smklen | legacy (`mklencom` [`sendcommands.cpp:95-102`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L95-L102)) | — | 8 |
| 3 | swrite | legacy (`datacom` [`:113-119`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L113-L119)) | — | 12+len |
| 4 | sclose | legacy (`closecom` [`:282-286`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L282-L286)) | — | 4 |
| 5 | sbackup | `sendbackup` noack ([`:300-302`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L300-L302)) | no | 4 |
| 6 | srenum | commented out | — | → CLOSE |
| 7 | sbackupstop | `sendbackupstop` noack ([`:293-295`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L293-L295)), not called anywhere | no | 4 (then CLOSE) |
| 8 | srender | `sendrender(pass,type)` ([`:338-342`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L338-L342)) | yes | 4 |
| 9 | snewnums | unused | — | → CLOSE |
| 10 | snums | `Numdata::update` ([`nums/numdata.hpp:1365-1398`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1365-L1398)) | yes | totlen |
| 11 | snumnr | `Numdata::sendlastpos` ([`numdata.hpp:1205-1209`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1205-L1209)) | yes | 8 |
| 12 | sack | appended by sendcommand | — | 4 |
| 13 | snuminit | `Numdata::sendbackupinit` ([`numdata.hpp:1245-1266`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1245-L1266)) | yes | 16 |
| 14 | sasklastnum | `Numdata::backupsendinit` noack ([`numdata.hpp:1267-1310`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1267-L1310)) | no (expects int reply) | 4 |
| 15 | saskfile | `SensorGlucoseData::getfromfile` noack ([`SensorGlucoseData.hpp:1989-2009`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1989-L2009)) | no (expects data reply) | 12+namelen, padded to 4 |
| 16 | suptodate | `noacksendone(suptodate)` ([`datbackup.cpp:235-237`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L235-L237)) | no | 4 |
| 17 | sfileonce | `Connect::senddata` ([`sendcommands.cpp:346-383`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L346-L383)) | yes | totlen |
| 18 | swakeupstream | `sendwakeupstream` noack ([`:303-305`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L303-L305)) | no | 4 |
| 19 | sglucose | `sendshowglucose` ([`:332-336`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L332-L336)) only under `#ifdef SENDSHOW` (never defined in this tree) | yes | 4 |
| 20 | sresetdevices | `sendResetDevices` noack ([`:296-300`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L296-L300)) | no | 4 |
| 21 | sStartSendCalibrate | `sendStartSendCalibrate` ([`:316-319`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L316-L319)); no C++ caller (maybe Java) | yes | 4 |
| 22 | sBlueWatch | `sendBlueWatch` ([`:320-329`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L320-L329)), WearOS only | yes | 4 → on server: CLOSE |
| 99 | stest | — | — | → CLOSE |

### 2.1 `sack` = 12 ([`getcommand.cpp:161-171`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L161-L171))
Layout `struct sendack {uint16 com=12; uint16 nop=1;}` ([`comtypes.hpp:125-128`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L125-L128)), 4 bytes.
Action: send raw uint32 `ackres = 917521` (`11 00 0E 00`) with `r_sendni` — **plain, never
encrypted**, also in password mode. Allowed even if host is not configured to receive.

### 2.2 `sopen` = 1 ([`getcommand.cpp:172-181`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L172-L181), [`64-87`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L64-L87))
`struct fileopen {uint16 com; uint16 rest; uint32 len; char name[len+1];}` ([`comtypes.hpp:139-144`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L139-L144)),
comlen = 8 + len + 1 (name is NUL-terminated, `len` excludes NUL).
Action: `filedata.open(name)` = mkdir -p parent (0700) + `open(<datadir>/name, O_RDWR|O_CREAT, 0600)`
(`getdata::open`, [`net/receive.hpp:49-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/receive.hpp#L49-L62)).
Reply: 4 bytes `{int16 fd; int16 (~fd & 0xFFFF)}`. Plain mode: raw 4 bytes. Password mode:
`sendcrypt` = `tag[16]` followed by 4 bytes ciphertext (**no length prefix**, unlike other
replies), encrypted with the *same* Ascon ctx that is decrypting the stream ([`getcommand.cpp:49-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L49-L62)).
The fd is a real server file descriptor, later referenced by smklen/swrite/sclose.
Modern app never sends this (sender side commented out) — implement only for completeness.

### 2.3 `smklen` = 2 ([`getcommand.cpp:182-188`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L182-L188))
`struct mklen {uint16 com; int16 han; uint32 len;}` 8 bytes. Action: ensure file `han` has
size ≥ len (sparse: write 1 byte at len-1) (`getdata::mkfile`, [`net/receive.hpp:73-86`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/receive.hpp#L73-L86)). No reply.

### 2.4 `swrite` = 3 ([`getcommand.cpp:216-226`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L216-L226))
`struct offwrite {uint16 com; int16 han; uint32 off; uint32 len; uint8 data[len];}` header
12 bytes, comlen = 12+len. Action: `pwrite(han, data, len, off)` (`getdata::savedata`, [`net/receive.hpp:102-114`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/receive.hpp#L102-L114)). No reply.

### 2.5 `sclose` = 4 ([`getcommand.cpp:227-233`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L227-L233))
4 bytes `{uint16 com; int16 han}`. Action: `close(han)`; failure ⇒ CLOSE connection.

### 2.6 `sbackup` = 5 and `sbackupstop` = 7 ([`getcommand.cpp:304-315`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L304-L315); [`datbackup.cpp:311-316`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L311-L316))
4 bytes (code + 2 unused bytes). Allowed even if host is not a receive-from host.
Action: `wakeupall()` → `backup->wakebackup(wakeall|wakereconnect)` = wake **all of this
server's sender threads** (forwarders), forcing them to close & reopen their outgoing
connection (`notpassive`, [`datbackup.hpp:1300-1313`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1300-L1313)) and do a full update.
* `sbackup`: ret=true → continue. Meaning: "I (a phone that receives from you) want data
  now" — this is what a phone configured as receiver with "reconnect" sends
  (`sendwakesender`, [`datbackup.hpp:1207-1225`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1207-L1225), and `sendup`, [`datbackup.cpp:395-432`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L395-L432)).
* `sbackupstop`: falls through with ret=false → after waking, the **connection is closed**.
No reply in either case.

### 2.7 `srender` = 8 ([`getcommand.cpp:269-284`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L269-L284))
`struct renderstruct {uint16 com; uint16 type;}` 4 bytes. Action:
`type ? backup->wakebackup(type) : backup->wakebackup()` (default `wakeall`=8), then `render()`
(no-op on server). `type` is a bitmask of wake kinds ([`datbackup.hpp:223-233`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L223-L233)):
`wakestream=1, wakescan=2, wakenums=4, wakeall=8, wakeUpSwitch=1024` (others: wakestop=16,
wakeother=32, wakeend=64, wakesend=128, wakereconnect=256, wakestreamsend=512).
The phone sends it **at the end of every update batch** with the bits of what it actually sent
(`doupdates`, [`datbackup.hpp:1391-1403`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1391-L1403)). This is THE trigger that makes the server forward the
just-received data to its own downstream phones. Ack: yes.

### 2.8 `snums` = 10 ([`getcommand.cpp:207-215`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L207-L215); [`nums/numdata.cpp:104-108`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L104-L108); [`numdata.hpp:1422-1471`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1422-L1471))
```
struct numsend {            // comtypes.hpp:163-171, header 16 bytes
  uint16 type;   // =10
  uint8  dbase;  // 0 or 1 (see §4.1)
  uint8  nr;     // number of blocks
  uint32 totlen; // whole command length incl. header
  uint32 first;  // NOT set by sender (garbage) – ignore
  uint32 last;   // sender's new lastpos (record count) for this dbase
  // nr times:
  //   uint32 off;     record index (absolute, record 0 = file byte 256)
  //   uint32 count;
  //   Num    recs[count];   16 bytes each
};
struct Num { uint32 time; uint32 mealptr /*|librenr|calibration:31,exclude:1*/; float value; uint32 type; } // nums/num.h
```
comlen = totlen (read from offset 4). Blocks are emitted newest-span-first
([`numdata.hpp:1377-1391`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1377-L1391)); order is irrelevant for a store.
Server action (`backupnums`, [`numdata.hpp:1422-1471`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1422-L1471)) on `numdatas[dbase]`:
1. for each block: `setonlychangetimes` (changetimes.dat[i]=now for records that differ),
   `addmorelibrenumschanged` (LibreView bookkeeping), `memcpy` records into nums file at
   byte `256 + 16*off`, `setlastchange(off)`, `nightBack(off)` (Nightscout upload cursor),
   **`updateposnowake(off, off+count)`** (mark range for forwarding, §5.1),
   `receivedbackup()=true` (header byte 16).
2. if `last < old lastpos` → changetimes[last..old) = now; `librechangelastpos(last)`.
3. `setlastpos(last)` (header @128), `setlastpolledpos(last)` (header @120).
4. **`backup->wakebackup(wakenums)`** (forward immediately).
Returns true ⇒ (phone's) ack. If `numdatas.size()<2` returns false ⇒ CLOSE. `dbase` is not
range-checked (≥2 = out-of-bounds in C++; reimpl should reject/close).

### 2.9 `snumnr` = 11 ([`getcommand.cpp:189-196`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L189-L196); [`numdata.cpp:96-102`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L96-L102); [`numdata.hpp:562-571`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L562-L571))
`struct lastpos_t {uint16 type=11; uint16 dbase; uint32 lastpos;}` 8 bytes.
Action: `numdatas[dbase]->setlastpos(lastpos)`; if it decreased, changetimes[new..old)=now
(entries deleted at the tail). **No wake, no forward-range marking**; downstream forwarding
notices `lastlastpos != lastpos` at the next nums wake ([`numdata.hpp:1351-1363`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1351-L1363)) and forwards an
`snumnr` itself. Sent by the phone when only the count changed (deletions). Ack: yes.

### 2.10 `snuminit` = 13 ([`getcommand.cpp:197-206`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L197-L206); [`numdata.cpp:109-113`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L109-L113); [`numdata.hpp:1213-1243`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1213-L1243))
```
struct numinit { uint16 com=13; uint16 res; uint32 first; int64 ident; }  // 16 bytes, ident @8
```
Target = `numdatas[(bool)ident]` (ident 0 → dbase 0 "here", ≠0 → dbase 1 "watch").
Action: if `ident != this->ident` → write `<datadir>/nums/watch/devicenr` = 8-byte ident (note:
always the *watch* dir, and in-memory ident is not changed); then `setfirstpos(first)`
(header @144). lastpos untouched. Sent by phone at (re)start of a mirror session when it
starts sending from the beginning ([`datbackup.cpp:154-160`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L154-L160)). Ack: yes.

### 2.11 `sasklastnum` = 14 ([`getcommand.cpp:340-352`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L340-L352); [`numdata.cpp:122-126`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L122-L126))
`struct asklastnum {uint16 com=14; uint16 dbase;}` 4 bytes.
Reply: int32 `numdatas[dbase]->getlastpos()` via `r_noacksendcommand` ([`sendcommands.cpp:403-408`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L403-L408)):
plain mode = raw 4 bytes; password mode = standard encrypted message
`tag[16] + enc({int32 len=4; int32 lastpos; 8 zero bytes})` (32 bytes total; no ack inside).
`dbase >= numdatas.size()` ⇒ -1 ⇒ CLOSE (no reply).
Phone use ([`numdata.hpp:1267-1310`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1267-L1310)): when it was told to send "from time T" it asks where our
copy ends; if `pos <= its firstpos` it starts over with `snuminit`, else it resumes at
min(pos, its lastpos) / first record ≥ T. **A server with an empty store should answer 0.**

### 2.12 `saskfile` = 15 ([`getcommand.cpp:327-339`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L327-L339), [`852-876`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L852-L876))
```
struct askfile { uint16 com=15; uint16 namelen /*incl NUL*/; uint32 off; uint32 len; char name[namelen]; }
// 12-byte header; phone pads the command to a multiple of 4 (SensorGlucoseData.hpp:1992)
```
Reply (`Connect::sendfile`): buffer `totlen = align4(4+len)`:
`struct dataonly { int32 n; uint8 data[len]; pad }` where `n = read(fd, …, len)` (may be
short), or `n = -1` if the file cannot be opened / lseek fails (data bytes then undefined —
send zeros). Sent with `r_noacksendcommand` (plain: raw totlen bytes; crypt: `tag[16] +
enc({int32 totlen; dataonly…; zero pad to ≥16})`). The phone reads exactly
`align4(4+asklen)` ([`getcommand.cpp:620-626`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L620-L626) / receivedatapass_alg [`:627-667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L627-L667)).
Phone use: `setbackuptime` ([`SensorGlucoseData.hpp:2081-2140`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2081-L2140)) asks
`sensors/<name>/info.dat`, off 0, len `offsetof(Info,pollinterval)`; it uses the answer only if
`n >= offsetof(Info,pollcount)` (reads `lastscantime`, `scancount`, `endhistory`, `pollcount`).
Quirk/bug: the "complete?" check compares the cursor with `ask->len` instead of the buffer
length ([`:333-336`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L333-L336)) — harmless in encrypted mode; a reimpl should simply require
`12+namelen` bytes.
No path sanitisation in the original (name used as is).

### 2.13 `suptodate` = 16 ([`getcommand.cpp:285-293`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L285-L293); [`datbackup.cpp:33-45`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L33-L45))
4 bytes. Action: `lastuptodate[allindex] = time(nullptr)` (status display "last synced").
Sent (no ack) at the end of every full `updateone::update()` ([`datbackup.cpp:235-237`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L235-L237)). No reply.

### 2.14 `sfileonce` = 17 ([`getcommand.cpp:354-372`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L354-L372), [`798-849`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L798-L849)) — the main data carrier
```
struct fileonce_t {            // comtypes.hpp:83-94, header 12 bytes
  uint16 com=17;
  uint16 namelen;              // incl. terminating NUL
  int32  totlen;               // whole command length (header..extra)
  uint16 nr;                   // number of ranges
  uint16 dowith;               // 0 or hint, see below
  struct { int32 off; int32 len; } gegs[nr];   // 8 bytes each
  char   name[namelen];        // relative path, NUL-terminated, e.g. "sensors/E07A-XXXXXXXXXXXX/polls.dat"
  uint8  data[sum(len)];       // the ranges' bytes, back to back, unaligned
  zero pad to multiple of 4;
  uint8  extra[extralen];      // optional (only with dowith≠0): 4 bytes int32, or 5 bytes CaliUpdateFrom
};
// totlen = align4(12 + 8*nr + namelen + sum(len)) + extralen   (sendcommands.cpp:346-383)
```
Parsing: if ≥ 8 bytes available, comlen = totlen, else 12 ([`:357-364`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L357-L364)).
Action (`savefileonce`):
1. `fp = open(<datadir>/name, O_RDWR|O_CREAT, 0600)` with mkdir -p of parents (0700).
2. For each range i: `pwrite(fp, data_i, len_i, off_i)`; failure ⇒ CLOSE. Close fp.
3. If `dowith != 0`, `sendindex = dowith & 0x3FFF`, extra located at
   `align4(end of data)` and its size = `totlen - that offset`:
   * `(dowith & 0xC000) == 0xC000` → **startcalibratedupdate** ([`net/backup.hpp:92`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.hpp#L92)):
     extra = `struct CaliUpdateFrom {uint32 updatefrom; bool history;} __packed` (5 bytes,
     [`backup.hpp:94-97`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.hpp#L94-L97)); older senders: 4-byte int (history=0).
     → `setcalibratedstart(sendindex, updatefrom, history)` ([`glucose.cpp:524-531`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L524-L531)):
     `backcalibrated` lowers `info.calis[history].caliUpdated[i]` for every downstream host i,
     and `setCalibrates(sendindex)` lowers `tosend[i].startSendCalibrate` for hosts with sendnums.
   * else if `dowith & 0x8000` → **streamupdatebit**: extra = int32 streamstart (else -1) →
     `processglucosevalue(sendindex, streamstart)` ([`cmdline/main.cpp:891-930`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L891-L930)):
     `backstream(pos)` ([`glucose.cpp:46-57`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L46-L57)) sets `info.update[i].changedstreamstart=true` and
     lowers `info.update[i].streamstart` to pos for each downstream host; then, if the
     sensor's newest stream value is < `maxbluetoothage` (330 s) old and the sensor is marked
     `finished` in sensors.dat, clear `finished` and `resensordata(sendindex)`.
   * else if `dowith & 0x4000` → **starthistoryupdate**: extra = int32 histstart →
     `sethistorystart` → `backhistory(pos)` ([`glucose.cpp:26-35`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L26-L35)): `changedhistorystart=true`,
     lower `info.update[i].histstart`.
   * negative/missing start (extra size ≠ 4) ⇒ nothing (setbackupstart ignores <0).
4. If `dowith == 0 && nr == 4 && name starts with "settings.dat"` →
   `askReinitScriptFonts()` (UI only).
Returns true ⇒ (phone's) ack.

Note: `sendindex` is the **phone's sensor index**, used directly as the server's index —
valid because sensors/sensors.dat is mirrored byte-for-byte (same index → same sensor).

### 2.15 `swakeupstream` = 18 ([`getcommand.cpp:316-326`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L316-L326); [`datbackup.cpp:317-322`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L317-L322))
4 bytes, allowed without receive permission. Action: `backup->wakebackup(wakestream|wakereconnect)`.
No reply. (Counterpart of sbackup for stream-only wake, [`datbackup.hpp:1227-1244`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1227-L1244).)

### 2.16 `sglucose` = 19 ([`getcommand.cpp:234-244`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L234-L244))
`renderstruct {uint16 com; uint16 sensorindex}`. Action: `processglucosevalue(sensorindex, -1)`
(un-finish sensor if fresh stream, see 2.14). Only sent if the app is built with SENDSHOW.

### 2.17 `sresetdevices` = 20 ([`getcommand.cpp:294-303`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L294-L303), [`113-123`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L113-L123))
4 bytes. Action `updateDevices()`:
1. `sensors->deletelast()` ([`sensoren.hpp:308-327`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L308-L327)): if the in-memory object of the last sensor
   exists and is "unused" (pollcount==scancount==endhistory==0), destroy it so it is re-opened
   from the now-written files on next access.
2. `sensors->setindices()` ([`sensoren.hpp:1816-1829`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1816-L1829)): recompute the `next/prev` linked list
   in sensors.dat (ordered by starttime).
3. `backup->resendResetDevices()` ([`datbackup.hpp:370-380`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L370-L380)): `tosend[i].resetdevices=true` for
   every downstream host with `sendscans` (they'll get `sresetdevices` too).
4. `sendstartsensors(sensors->last())` ([`datbackup.cpp:824-833`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L824-L833)): lower `tosend[i].startsensors`.
5. `javaUpdateDevices()` → true on server.
Sent (no ack) after a sensors.dat update that introduced new devices ([`sensoren.hpp:1723-1732`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1723-L1732))
and after `update()` when `resetdevices` is set ([`datbackup.cpp:238-245`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L238-L245)). No reply.

### 2.18 `sStartSendCalibrate` = 21 ([`getcommand.cpp:257-268`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L257-L268); [`datbackup.cpp:836-846`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L836-L846))
`struct uint16_t_arg_struct {uint16 com; uint16 arg /*sensorindex*/}`. Action: for every
downstream host with `sendnums`: `startSendCalibrate = min(startSendCalibrate, arg)`. Ack: yes.

### 2.19 `sBlueWatch` = 22 ([`getcommand.cpp:245-256`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L245-L256))
`struct bluewatchstruct {uint16 com; int8 stream; int8 nums;}`. Only compiled with
`JUGGLUCO_APP` (`setBlueWatch`). On the server build ⇒ `default` ⇒ **CLOSE**. Allowed past the
receive-permission gate. Sent only by WearOS watches ([`datbackup.cpp:129-138`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L129-L138)).

---------------------------------------------------------------------------------------

## 3. Which files arrive (relative to the data directory)

Data dir = `globalbasedir` (`-d`/config dir of the server); all received paths are relative
to it (`backupbase(globalbasedir)`, [`datbackup.cpp:896-897`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L896-L897); [`getcommand.cpp:89-91`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L89-L91)).
Setup in [`settings/settings.cpp:190-200`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L190-L200), [`520-533`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L520-L533).

<pre>
&lt;datadir&gt;/
  settings.dat                       Tings struct (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.hpp#L181">settings.hpp:181…</a>), mmapped by server; RECEIVED (ranges)
  meals.dat                          mealdata (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L40-L58">meal/Meal.hpp:40-58</a>), mmapped; RECEIVED (ranges)
  backup.dat                         server-local mirror config/cursors (updatedata) – never received
  nums/here/nums0                    dbase 0 nums file (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L238-L240">numdata.hpp:238-240</a>, <a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L530">settings/settings.cpp:530</a>)
  nums/here/{changetimes,libreids,librechanged}.dat   server-maintained side tables
  nums/watch/nums-1                  dbase 1 nums file
  nums/watch/devicenr                int64 ident (written by snuminit)
  nums/watch/{changetimes,libreids,librechanged}.dat
  sensors/sensors.dat                sensor list; RECEIVED via sfileonce "sensors/sensors.dat" (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1576">sensoren.hpp:1576</a>)
  sensors/&lt;16-char-name&gt;/info.dat    Info struct (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L386">SensorGlucoseData.hpp:386-…</a>); RECEIVED (ranges)
  sensors/&lt;name&gt;/polls.dat           stream values, ScanData[20 B] (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L142">SensorGlucoseData.hpp:142</a>); RECEIVED
  sensors/&lt;name&gt;/data.dat            history, element 4+(dupl+1)*2 B; RECEIVED
  sensors/&lt;name&gt;/current.dat         scans, ScanData[]; RECEIVED (Libre 1/2 scans)
  sensors/&lt;name&gt;/trends.dat          uint16[16] per scan; RECEIVED
  sensors/&lt;name&gt;/state.lnk + &lt;state file&gt;   Libre2 algorithm state (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L394-L418">glucose.cpp:394-418</a>); RECEIVED whole
  sensors/&lt;name&gt;/state.bin           Sibionics state (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2231-L2241">SensorGlucoseData.hpp:2231-2241</a>); RECEIVED whole
  sensors/&lt;name&gt;/sensorInfo, generated   CareSens Air (<a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2242-L2253">SensorGlucoseData.hpp:2242-2253</a>, <a href="https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2374">2374</a>); RECEIVED whole
  logs/                              server local
</pre>
Sensor directory names are the 16-char names stored in sensors.dat records (e.g.
`E07A-XXXX<sensorid>` for Libre 3, [`sensoren.hpp:378-386`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L378-L386)). `baseuit` = path relative to data
dir ([`SensorGlucoseData.hpp:1506-1519`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1506-L1519)).

What the phone writes into each file (all via sfileonce):
* **sensors/sensors.dat** ([`sensoren.hpp:1663-1722`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1663-L1722)): file = `infoblock{int32 last,current,markold,start,version}` padded to 32 B
  + `sensor[32 B]` records ([`sensoren.hpp:48-92`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L48-L92)). Phone sends records `[changed..last]`; when
  `changed==0` it sends `last` at off 0 and the rest from off 4 (2 ranges). Followed by
  `sresetdevices` if new devices.
* **info.dat**: many small ranges — counters (pollcount, scancount, endhistory, starthistory…),
  device names/addresses/keys, calibration params (`calis[i].caliPara`, `caliNr`), `broadcastfrom`.
  The phone NEVER writes `Info::update[]` (per-downstream-host forward cursors) nor
  `calis[i].caliUpdated[]` — those bytes in the server's info.dat belong to the server.
* **polls.dat / data.dat / current.dat / trends.dat**: element ranges `[start..end)`.
* **settings.dat** ([`datbackup.cpp:47-125`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L47-L125), [`196-224`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L196-L224), [`257-269`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L257-L269)): ranges of Tings:
  `nochangenum`(forced true), `bloodvar`, `[update .. mealvar]` (labels/variables/shortcuts;
  `update` is a change counter), `gs3id`, `[insulintypes .. iobupdate]`, `Nightnums`,
  `librenums`, `jugglucoID`. **These overwrite the server's own settings at the same offsets**
  (the server's settings.dat is a live mmap, [`settings.cpp:206`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L206)).
* **meals.dat** ([`Meal.hpp:133-156`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L133-L156)): header fields, `gotlastmeal` set to the phone's resend
  start, `mealindex`+units, ingredients, `themeals[startmeal..mealindex)`.

---------------------------------------------------------------------------------------

## 4. Nums store

### 4.1 Which dbase is which
* Server opens `numdatas[0] = nums/here` (ident 0) and `numdatas[1] = nums/watch` (ident -1 /
  devicenr) — order fixed ([`settings.cpp:530-531`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L530-L531), "order important!!").
* Phone sends `dbase = (bool)ident` of its own Numdata ([`numdata.hpp:1337`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1337), [`1272`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1272)). Phone "here"
  (its own entries) → server dbase 0 "here"; phone "watch" → server dbase 1. I.e. the mirror is a
  1:1 copy of both databases. (`getindex()` = 1 for ident 0, 0 otherwise is only the index into
  `changednums nums[2]` of each forward host — reversed, don't confuse.)

### 4.2 Nums file layout ([`numdata.hpp:74-88`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L74-L88), [`465-470`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L465-L470); [`num.h`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/num.h))
256-byte header, then `Num[16 B]` records; record i at byte `256+16*i`.
Header offsets: 0 magic `FE D2 DE AD`; 16 receivedbackup(bool); 32 newident(int64);
120 lastpolledpos; 128 **lastpos**; 136 changedpos; 144 **firstpos**; 152 onechange;
160 libresend; 164 librechangednr; 168 libreSolid; 172 nightStart; 176 nightSend;
180 nightSwitch; 184 nightIDstart (all int32).
Valid records: `[firstpos, lastpos)`. Initial mmap 77056 records ([`numdata.hpp:58`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L58)).
Deleted/changed entries are modified in place (type gets `removedbit`, etc.) and re-sent as
an `snums` block covering that index.

---------------------------------------------------------------------------------------

## 5. In-memory / persisted state updated on receive (needed for forwarding)

The server forwards to its downstream hosts ("tosend[i]", `updateone`, [`datbackup.hpp:119-177`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L119-L177);
persisted in backup.dat) by running exactly the same sender code as a phone. That code works
from cursors; the receive handlers only *lower* cursors and *wake* threads:

| Received | Cursor lowered / flag set | Wake |
|---|---|---|
| snums block [off,off+n) | `tosend[h].nums[getindex()]` changed spans via `updateposnowake` ([`numdata.hpp:1110-1159`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1110-L1159)) | `wakebackup(wakenums)` immediately |
| snumnr | nothing (detected via `nums[].lastlastpos != lastpos`, [`numdata.hpp:1353`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1353)) | none (comes with srender) |
| snuminit | `firstpos` only | none |
| sfileonce info.dat + dowith stream | `info.update[h].streamstart`, `changedstreamstart` | none (srender) |
| sfileonce + dowith history | `info.update[h].histstart`, `changedhistorystart` | none |
| sfileonce + dowith calibrated | `info.calis[x].caliUpdated[h]`, `tosend[h].startSendCalibrate` | none |
| sfileonce sensors.dat | nothing explicit: forwarder compares its `last()` with `tosend[h].startsensors` ([`sensoren.hpp:1663`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1663)) | none |
| sfileonce settings.dat | the received `Tings::update` counter > `tosend[h].updatesettings` triggers resend ([`datbackup.cpp:196-224`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L196-L224)) | none |
| sfileonce meals.dat | received `gotlastmeal` field itself is the resend cursor ([`Meal.hpp:133-156`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L133-L156)) | none |
| sresetdevices | deletelast, setindices, `tosend[h].resetdevices`, `startsensors` | none |
| sStartSendCalibrate | `tosend[h].startSendCalibrate` | none |
| sglucose / stream dowith | sensors.dat `finished=0`, `tosend[h].firstsensor`/`startsensors` (resensordata, [`datbackup.hpp:355-367`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L355-L367)) | none |
| **srender(type)** | — | `wakebackup(type)` (or wakeall) |
| sbackup / sbackupstop | — | `wakebackup(wakeall|wakereconnect)` |
| swakeupstream | — | `wakebackup(wakestream|wakereconnect)` |
| suptodate | `lastuptodate[allindex]=now` | none |

`wakebackup` ([`datbackup.hpp:1443ff`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1443), condvar [`:260-273`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L260-L273)) ORs the bits into each sender thread's
`dobackup` and notifies; `doupdates` ([`datbackup.hpp:1391-1403`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1391-L1403)) then runs updatenums /
updatestreamu / updatescansu / updateBeforeSwitch / update for the set bits and finally sends
`srender(bits)` downstream (so chains of mirrors propagate).

Because sensors.dat, info.dat, settings.dat, meals.dat and the nums files are **mmapped
MAP_SHARED** by the server ([`inout.hpp:311-352`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/inout.hpp#L311-L352)) and received data is written with `pwrite` to
the same files, the in-memory views see received bytes immediately — there is no explicit
"reload" except `deletelast()` on `sresetdevices` and lazy `getSensorData(i)` creation
([`sensoren.hpp:1206-1250`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1206-L1250)), which also writes `present=1` and `starttime` back into the
server's sensors.dat record.

Connection-level state touched by the receive side (handshake area, for completeness):
if the sender's magic ends with byte 0 (its "new connection"), the receiver calls
`resethost` → `clearhost(host.index)` ([`datbackup.hpp:556-586`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L556-L586), [`693-699`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L693-L699)) = reset all forward
cursors *towards that same host* (only if it is also a send target) ([`net/backup.cpp:325-330`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L325-L330)).

---------------------------------------------------------------------------------------

## 6. Can a reimplementation be an opaque store-and-forward? — Assessment

**Receive side: yes, almost entirely opaque.**
* `sfileonce`/`swrite`: write byte ranges into `<datadir>/<path>`; no need to understand the
  content. Must create parent dirs and support sparse writes at arbitrary offsets. The
  `dowith` hint + extra need only be parsed (12 lines of code) if you forward.
* Nums: store 16-byte records per (dbase, index) + `firstpos`, `lastpos` per dbase. No need to
  interpret the record fields.
* Replies needed: ack (constant), `sasklastnum` (= stored lastpos of that dbase, 0 if empty),
  `saskfile` (= raw bytes of the stored file range, n=-1 if absent), `sopen` (only legacy).
* No command requires the server to understand glucose semantics in order to answer correctly.

**Forwarding side: opaque is possible, but NOT by naive whole-file copying.**
* `info.dat` contains bytes owned by the *receiver* (`Info::update[]` forward cursors,
  `calis[].caliUpdated[]`, and other local fields). Sending a whole info.dat to a downstream
  Juggluco would overwrite that phone's own cursors. Forward only byte ranges that were
  themselves received (e.g. keep a journal of received `sfileonce` ranges per file, or a
  per-file "received range" bitmap) — the original phone never sends those cursor regions, so
  replaying received ranges is safe.
* Same for `settings.dat`: forward only the received ranges, never the whole file
  (downstream's own settings live there).
* A journal/replay design ("forward every received command, in order, to each downstream
  host, per-host offset into the journal, with ack") is the simplest compatible approach:
  sfileonce / snuminit / snums / snumnr / sresetdevices / srender can be relayed verbatim
  (the `dowith` hint is meaningful downstream too, since sensor indices are identical
  everywhere). Commands that must NOT be relayed verbatim: `sack` (regenerate per hop),
  `sasklastnum`, `saskfile` (requests addressed to us), `suptodate` (per-link), `sbackup*`,
  `swakeupstream`, `sopen/smklen/swrite/sclose` (fd-based; convert to sfileonce).
* Bootstrapping a new downstream phone from an opaque store: send `snuminit`+full `snums`
  for both dbases, and sfileonce of all *mirror-owned* ranges (from the journal / range map).
  The original server instead re-derives everything from its parsed files (`sensors->update`,
  `Info` counters) — that requires the struct layouts (Info, sensor, Tings, mealdata), which
  are version-dependent.
* If you also want Nightscout/xDrip web output, you DO need to parse polls.dat/ScanData,
  sensors.dat and nums (that is what the original server's webserver does).

---------------------------------------------------------------------------------------

## 7. Uncertain / needs testing against the real app

1. **Command set of the current Android app.** This tree has codes 1-22 and 99. If a newer app
   sends a new code, the server (any `default:`) silently closes the connection. Capture real
   traffic (password-less mode is easiest) to confirm which codes appear: expected
   {8,10,11,12,13,14,15,16,17,18,20,(21),(5)}; 1-4 should never appear.
2. **Ack is plaintext in password mode** ([`getcommand.cpp:167`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L167) uses raw `r_sendni`; phone's
   `getack` uses raw `s_recvni`). Looks deliberate but verify on the wire.
3. **Reply encryption state**: `sopen`, `sasklastnum`, `saskfile` replies are encrypted with
   the *same* Ascon ctx that decrypts the incoming stream, after a `decrypt_final`
   ([`getcommand.cpp:49-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L49-L62), [`351`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L351), [`875`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L875)). Exact ctx continuation semantics → spec-1 / test.
4. `sopen` reply in password mode has **no length prefix** (tag+4 bytes), unlike the others.
5. `saskfile` for a missing/short file: original returns n=-1 (garbage data) or a short n.
   The phone only uses it if n ≥ offsetof(Info,pollcount). Verify the app copes with n=-1
   (it should: `dat->len>=minlen` check) and with zero-padded data.
6. `sasklastnum` on an empty store: answer 0 (original returns lastpos=0 for a fresh file).
   Check that the phone then restarts with `snuminit` (pos<=firstpos branch).
7. `snuminit` ident handling: writes `nums/watch/devicenr` even for dbase 0 if idents differ
   (only possible if ident≠0, i.e. dbase 1 anyway). Confirm ident value sent by phone for
   its "watch" db (-1? random?).
8. `snums.first` is uninitialized on the sender — never rely on it.
9. `dbase` values ≥ 2 are not validated in C++ (UB). Reimpl: close connection.
10. `CaliUpdateFrom` = 5 bytes packed ⇒ fileonce `totlen` not a multiple of 4; the receiver
    re-aligns. Make sure the extra-size detection (4 vs 5 bytes) is done as
    `totlen - align4(offset_after_data)`.
11. Path names are not sanitised in the original (`..`, absolute paths). A reimpl should
    reject them; verify the app never sends such names (it shouldn't: all are
    `settings.dat`, `meals.dat`, `sensors/...`).
12. Receive-permission gate: commands other than {5,7,12,18,22} from a host without
    `receivefrom&2` close the link. The reimpl's host config must mark the phone as
    "receive from".
13. `sBlueWatch` from a WearOS watch closes the connection on the original server — confirm
    whether a watch ever connects to a server as *sender*.
14. `sStartSendCalibrate` and `sglucose`: no C++ sender caller (SENDSHOW undefined); may be
    sent from Java code of the app — handle them anyway (trivial).
15. Struct-size compatibility (Info, Tings, sensor, mealdata) between app version and server
    only matters if the reimpl parses files; an opaque store is version-agnostic.
16. Ordering assumption: the ack must be sent only after all preceding commands have been
    durably applied (the phone advances its cursors on ack, e.g. `updateone::update`,
    `Numdata::update`, [`numdata.hpp:1401-1412`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1401-L1412)). Batch fsync policy is up to the reimpl.
17. Size limits: encrypted messages up to 256 MiB (`MAXDATA`, [`getcommand.cpp:480`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L480)); plain-mode
    commands may exceed 1 MiB (buffer grows). Large initial syncs (whole polls.dat ≈ MBs) occur.
