# Spec 3: SEND side of the Juggluco mirror protocol (server pushes data to passive-connecting phones)

Source: the Linux command-line build of Juggluco. Source references point to Juggluco at commit
[43fdbe1](https://github.com/j-kaltes/Juggluco/tree/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp)
(11.0.x); paths are relative to `Common/src/main/cpp`. "file:N" means line N; "file:N-M" is a range; a bare
line number refers to the file named last. This document covers only the **send** direction. Handshake/crypto
details and the receive side of the server are covered by other specs; here I only repeat what the send path depends on.

Terminology used below:

* **S**: the source phone that sends data to the server.
* **X**: the server (this code, or your reimplementation).
* **R**: a receiving phone configured on X as "send nums/scans/stream, passive only". R connects to X and X pushes data.
* **allindex**: index of the connection in `updatedata.allhosts[]` (max 8, [`maxsendtohost.h`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/maxsendtohost.h)).
* **sendindex / `ind`**: index in `updatedata.tosend[]` (max 6, `maxsendtohost=6`). Stored in `passhost_t.index` (-1 means "not a send target").
* **nums**: Juggluco's manual entries ("amounts" in the app's UI: insulin, carbs, fingersticks…), kept in two databases, "here" and "watch".
* **sensorindex**: index of the sensor in `sensors/sensors.dat`. It is identical on S, X and R because X relays `sensors.dat` byte-for-byte.

All integers are little-endian. Struct layouts assume the Android/Linux LP64 GCC/Clang ABI (natural alignment, bit-fields allocated from the LSB).

---

## 1. Connection lifecycle for a passive send target

### 1.1 Configuration (how a receiver becomes "passive send")
* `Backup::changehost(...)` ([`datbackup.hpp:806-1066`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L806-L1066)). `sendto = sendnums||sendstream||sendscans` ([`862`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L862)). `dontopen = sendto && passiveonly` ([`866`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L866)) leads to `thehost.sendpassive = dontopen` ([`884`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L884)).
* For a new target, `changereceiver()` ([`602-652`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L602-L652)) allocates `tosend[sendnr]`, calls `clearhost()` and sets `starttime = 1` (full-history init pending), or `starttime = <unix time>` when a "send from" time was given ([`621-626`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L621-L626)). It also stores `sendnums/sendstream/sendscans` ([`627-629`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L627-L629)) and creates a per-target `crypt_t` if a password is set ([`637-651`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L637-L651)).
* The `receivefrom` value of the host follows the table at [`datbackup.hpp:1014-1021`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1014-L1021). For a send-only passive target: `receive=false`, `reconnect = sendto && !activeonly = true`, which gives `receivefrom = 1`.
* One sender thread per send target is started: `startthread()` ([`datbackup.hpp:1495-1500`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1495-L1500)), which runs `backupthread()` ([`1249-1301`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1249-L1301)).

### 1.2 Accepting R's connection (X side)
`serverloop()` ([`net/backup.cpp:431-611`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L431-L611)):
1. `accept`. Sets SO_RCVTIMEO and SO_SNDTIMEO to 180 s ([`470-475`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L470-L475)).
2. Selects the `passhost_t`, either by IP or by the 16-byte label that R sends first ([`477-551`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L477-L551)). This is handshake territory, see the other spec.
3. `testreceivemagic(hit)` ([`backup.cpp:297-351`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L297-L351)). X receives R's send-magic and answers with the receive-magic. **If the last byte of R's magic is 0** (R marks itself as a "new connection"), X calls `resethost(*pass)` ([`325-330`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L325-L330)). That clears all per-receiver progress (see §6.4), which forces a full resend from position 0.
4. If `hit->sendpassive` ([`575`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L575)): `ant = SENDPASSIVE (1)` by default. Only if X **also receives** from this host (`receivedatafrom()` = `receivefrom&2`) **and** X does not send all three kinds (`!sendall(hit)`, [`datbackup.cpp:748-751`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L748-L751)) does X read **one extra byte** `ant` ([`579-586`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L579-L586)). With `ant==1`, X calls `con->passivesender(hit,sock,oldsock)` ([`591`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L591)) and the connection becomes X's *sender* socket. With any other value, the connection is treated as an incoming data connection ([`602-608`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L602-L608)).
   * On R's side that byte comes from `sayactivereceive()`: 1 if R itself sends to X, otherwise **no byte is sent at all** ([`datbackup.cpp:342-347`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L342-L347) together with [`sender.cpp:258-260`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L258-L260), where `stype==0` sends nothing).

### 1.3 `TCPConnect::passivesender` ([`datbackup.cpp:679-714`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L679-L714))
1. If the host has a password: `receivepassinit(pass, host.getcrypt())` ([`694`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L694)). X receives R's 8-byte nonce half, sends 8 random bytes, then calls `ascon_aead128a_init(ctx, pass, nonce[16])` ([`net/getcommand.cpp:692-725`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L692-L725)). The ctx is the **per-send-target** `crypts[ind]` ([`datbackup.cpp:349-360`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L349-L360)).
2. `setSenderSock(recsock)`. Any previous sender socket is shut down and closed ([`TCPConnect.hpp`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp) setSock). Then `setSenderTimeouts()`: recv timeout 60 s, send timeout 300 s, TCP_USER_TIMEOUT 94 s ([`TCPConnect.hpp:32-38`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp#L32-L38), [`sender.cpp:190-211`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L190-L211)).
3. `con_vars[h]->wakebackup(wakeall)` ([`709`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L709)). This wakes the sender thread, which then performs the **full update** (§4).

After this point, R runs `activegetcommands()` → `getcom()`/`getcommandsnopass()` on that socket ([`datbackup.cpp:541-555`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L541-L555), [`getcommand.cpp:745-752`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L745-L752)). It interprets whatever X sends.

### 1.4 Failure and reconnect
* Any send/ack failure in a step makes `updateproc()` close the sender socket. **For passive targets it does not retry and does not reconnect** ([`datbackup.hpp:1327-1365`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1327-L1365), specifically [`1336-1342`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1336-L1342)). Progress counters are only advanced after a successful send, so nothing is lost. X waits for R to reconnect, which triggers §1.3 again and a `wakeall`.
* X never sends keep-alives on an idle passive sender socket. R's side blocks in recv with TCP keepalive ([`backup.cpp:354-408`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L354-L408)).
* `updateone::open()` returns immediately for `sendpassive` ([`datbackup.cpp:377-378`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L377-L378)). X never dials out to a passive target.

---

## 2. Framing of what X sends (sender socket)

### 2.1 Primitives ([`net/sendcommands.cpp`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp))
| Primitive | Code | Behaviour |
|---|---|---|
| `sendcommand(pass,buf,len)` | [`263-268`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L263-L268) | Command **with ack** |
| `s_noacksendcommand(pass,buf,len)` | [`269-274`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L269-L274) | Command **without ack** |
| `sendone(pass, u32 com)` / `noacksendone` | [`285-292`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L285-L292) | 4-byte command (`u16 com, u16 0`) |
| `getack()` | [`171-185`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L171-L185) | Reads **4 plaintext bytes** (never encrypted) and requires `== ackres = 917521 = 0x000E0011` ([`comtypes.hpp:129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L129)) |

**Unencrypted with ack** ([`186-210`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L186-L210)):
`[buf (len bytes)] [pad to a multiple of 4 (uninitialised bytes)] [sendack = u16 12, u16 1]`. The pad and the sack are sent in a second `send()`. Then X waits for the 4-byte ack. On R, `interpret()` aligns to 4 after every command ([`getcommand.cpp:377`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L377)) and answers `sack` by writing `ackres` in plaintext ([`161-171`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L161-L171)).

**Encrypted** (`gensendcommandpass`, [`216-258`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L216-L258)):
* plaintext = `i32 comlen | buf | [zero pad to 4 | sendack]` (the bracketed part only with ack) `| zero padding until the plaintext is at least 16 bytes`.
* `comlen` = buflen (+pad+4 if ack).
* Wire = `[16-byte tag][ciphertext of the plaintext]`. The tag comes **first** (`encrypt_final(ctx, …, allbuf /*tag*/, 16)`).
* The Ascon context is **not reset between frames**. LibAscon was patched so that `encrypt_final` no longer cleans the context ([`LibAscon/src/ascon_aead128a.c:293`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L293)). The sponge state therefore continues from frame to frame.
* The **same ctx** is used to decrypt R's replies on this socket (saskfile and sasklastnum responses). Encrypt and decrypt operations interleave on one state, in protocol order. A reimplementation must reproduce this exactly; see the crypto spec.
* With ack, X then reads the 4-byte **plaintext** ack.

R reads an encrypted frame as: tag (16), the first 16 ciphertext bytes, decrypt, `datlen`, then the rest ([`getcommand.cpp:482-590`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L482-L590)). R reads the tag with a single `recv` (`recvni` does not loop, [`netstuff.hpp:122-133`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/netstuff.hpp#L122-L133)). **Recommendation: write every frame in a single send/write.**

### 2.2 Replies X may read on the sender socket
Only during an initial sync that uses a start time (`starttime > 1`, §4.2):
* After `saskfile`: R answers with `dataonly {i32 len; u8 data[len]}`, padded to 4. Unencrypted it is exactly `aligner4(4+asklen)` bytes. Encrypted it is a no-ack frame whose payload is that struct ([`getcommand.cpp:852-876`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L852-L876)). X reads it with `receivedataonly_s` ([`getcommand.cpp:620-626`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L620-L626), [`628-667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L628-L667)). `len == -1` means the file was missing.
* After `sasklastnum`: R answers with `i32 lastpos` in the same framing ([`getcommand.cpp:340-352`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L340-L352)). X reads it with `receivedata_s(pass, 4)`.

---

## 3. Commands X sends (codes: [`net/comtypes.hpp:26-48`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L26-L48))

| code | name | struct (bytes) | ack? | sent by |
|---|---|---|---|---|
| 8 | `srender` | `renderstruct{u16 com; u16 type}` (4) | yes | end of every `doupdates` that did something ([`datbackup.hpp:1399-1404`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1399-L1404)). `type` = OR of wake bits actually done (§5.1) |
| 10 | `snums` | `numsend{u16 type=10; u8 dbase; u8 nr; u32 totlen; u32 first(UNINITIALISED); u32 last; u32 nums[]}`, then for each span `u32 start, u32 count, Num[count]` | yes | [`numdata.hpp:1365-1397`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1365-L1397) |
| 11 | `snumnr` | `lastpos_t{u16 11; u16 dbase; u32 lastpos}` (8) | yes | [`numdata.hpp:1205-1209`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1205-L1209) |
| 13 | `snuminit` | `numinit{u16 13; u16 res=0; u32 first; i64 ident}` (16) | yes | [`numdata.hpp:1245-1266`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1245-L1266) |
| 14 | `sasklastnum` | `asklastnum{u16 14; u16 dbase}` (4) | no, answered with i32 | [`numdata.hpp:1271-1283`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1271-L1283) |
| 15 | `saskfile` | `askfile{u16 15; u16 namelen; u32 off; u32 len; char name[namelen]}`, total padded to 4 | no, answered with dataonly | [`SensorGlucoseData.hpp:1989-2009`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1989-L2009) |
| 16 | `suptodate` | u32 16 | **no** | [`datbackup.cpp:236`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L236) |
| 17 | `sfileonce` | see §3.1 | yes | `Connect::senddata` ([`sendcommands.cpp:346-393`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L346-L393)) |
| 20 | `sresetdevices` | u32 20 | **no** | [`datbackup.cpp:238-245`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L238-L245), [`sensoren.hpp:1724-1732`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1724-L1732) |

The following are **not** sent to passive targets by this code:
* `sbackup(5)`, `swakeupstream(18)`: only sent in `notpassive()` for active targets ([`datbackup.hpp:1303-1315`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1303-L1315), [`1208-1246`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1208-L1246)).
* `sbackupstop(7)`: never used.
* `sglucose(19)`: only with `#ifdef SENDSHOW` ([`sensoren.hpp:1734-1739`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1734-L1739)).
* `sBlueWatch(22)`: WEAROS builds only.
* `sStartSendCalibrate(21)`: never called on the send path.
* `sopen/smklen/swrite/sclose (1-4)`: old protocol, `sendopen` is commented out.

### 3.1 `sfileonce` (17): the universal "write byte ranges into a file" command
Layout ([`comtypes.hpp:83-94`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L83-L94), [`sendcommands.cpp:346-386`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L346-L386)):
```
u16 com=17
u16 namelen          // strlen(name)+1
i32 totlen           // = aligner4(12 + 8*nr + namelen + Σlen) + extralen
u16 nr               // number of segments
u16 dowith           // 0, or flag|sensorindex (see below)
{i32 off; i32 len}[nr]
char name[namelen]   // NUL-terminated, path relative to the data dir, e.g. "sensors/E07A-XXXX0123456/polls.dat"
u8 data[Σlen]        // segment payloads concatenated, in order
pad to 4 (garbage)
u8 extra[extralen]   // optional trailer (int32 startpos, or 5-byte CaliUpdateFrom)
```
`totlen` is not necessarily a multiple of 4 (extralen can be 5). The ack sack is placed after aligning to 4.

`dowith` meaning on R ([`getcommand.cpp:817-836`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L817-L836), [`backup.hpp:90-97`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.hpp#L90-L97)):

| dowith | trailer | effect on R |
|---|---|---|
| `0xC000\|sensorindex` (`startcalibratedupdate`) | `CaliUpdateFrom{u32 updatefrom; u8 history}`, packed, 5 bytes | `setcalibratedstart` |
| `0x8000\|sensorindex` (`streamupdatebit`) | `i32 streamstart` | `processglucosevalue(sensorindex, streamstart)`. **This is what makes R process and display or alarm the new glucose value.** |
| `0x4000\|sensorindex` (`starthistoryupdate`) | `i32 histstart` | `sethistorystart` |

The low 14 bits are the sensorindex (`dowith & 0x3FFF`, [`getcommand.cpp:759`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L759)). The trailer is recognised by size: `totlen - aligner4(end of data) == 4` (or `== 5` for calibrations), [`getcommand.cpp:754-783`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L754-L783).

The `T*` overload `senddata(pass, offset, startT, endT, name)` converts an element offset into a byte offset `offset*sizeof(T)` ([`Connect.hpp:114-119`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/Connect.hpp#L114-L119)).

---

## 4. Initial full sync of a newly connected receiver

When R connects, `passivesender` → `wakebackup(wakeall)` → `backupthread` → `doupdates(wakeall)`. With only `wakeall` set, `updatenums`, `updatestreamu`, `updatescansu` and `updateBeforeSwitch` are skipped (their condition bit is 0, so `updateproc` returns 2), and **`updateone::update()` ([`datbackup.cpp:123-247`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L123-L247)) runs**. The steps below are in exact order. Each sfileonce, snums, snumnr and snuminit waits for its ack before the next one is sent.

### 4.1 `starttime == 1` (new target, "send everything")
1. (WEAROS only: `sBlueWatch`. Not in the server.)
2. If `sendnums` and `nums[0].lastlastpos == 0 && nums[1].lastlastpos == 0`: for each nums database in `numdatas` order (`"here"` ident 0 first, then `"watch"`; [`settings.cpp:530-531`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.cpp#L530-L531)), send **`snuminit`** `{first = firstpos of that db, ident}` ([`numdata.hpp:1245-1266`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1245-L1266)). Also set `changed[0].start = lastlastpos = firstpos`.
3. Set `starttime = 0` ([`datbackup.cpp:181`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L181)).
4. **`updatenums()`** ([`251-275`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L251-L275)), only if `sendnums`:
   a. Once per target (`!sendjugglucoid`): sfileonce `settings.dat` with one segment `[offsetof(Tings,jugglucoID), 8]`.
   b. `::updatenums()` ([`nums/numdata.cpp:69-80`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L69-L80)). For each nums db, `Numdata::update()` sends **snums** (changed spans) or **snumnr** (lastpos only), or nothing (§5.3). Then `mealdata::updatemeal()` sends sfileonce `meals.dat` (§5.4).
   c. `sendCalibrate()` → `Sensoren::sendCalibrates()` ([`sensoren.hpp:1594-1614`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1594-L1614)). For each sensor from `startSendCalibrate` to `last()`, `updateCali()` sends sfileonce `info.dat` with dowith `0xC000|i` for each of the 2 calibration tables that changed ([`SensorGlucoseData.hpp:2574-2608`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2574-L2608)). Afterwards `startSendCalibrate = last()`.
5. If `sendstream || sendscans`: **`Sensoren::update(...)`** ([`sensoren.hpp:1615-1744`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1615-L1744)). For `i = firstsensor .. last()` (only sensors where `getSensorData(i)` succeeds; a NULL there **aborts** the whole update):
   * if `sendstream`: `updatestream(pass,connect,ind,i,0)` (§5.5, with sendscan=0, so no Libre-2 history here). For Sibionics also `sendSibionicsState()` (state.bin, §5.8).
   * if `sendscans`: `updatescan(pass,connect,ind,i, dotoch = (i >= startsensors), sendstream)` (§5.6).
   * Then **sensors.dat**. If `last() >= startsensors`, or `resetdevices`, it sends the whole file: two segments, `[0,4] = snapshot of last` and `[4, (last+2)*32 - 4]`. Otherwise, if some scan update returned 1 or 5, it sends from record `changed = i+1` to the end. Then `startsensors = last+1`. If any `updatescan` returned 5 ("new device info"), it also sends **`sresetdevices`** (no ack).
   * `firstsensor` becomes the first sensor that is not finished, or whose maxtime is still in the future ([`1641-1643`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1641-L1643), [`1740-1741`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1740-L1741)).
6. **settings.dat** if `Tings.update > updatesettings` ([`datbackup.cpp:199-223`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L199-L223)). One sfileonce `settings.dat` with:
   * if sendnums: `[offsetof(nochangenum),1]` (value **forced to true**), `[offsetof(bloodvar),1]`, and `[offsetof(update) .. offsetof(mealvar)+1)`.
   * if sendscans: `[offsetof(gs3id),12]`.

   Then `updatesettings = Tings.update`.
7. **`updateiob()`** ([`65-81`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L65-L81)): if `Tings.iobupdate > iobupdated`, sfileonce `settings.dat [offsetof(insulintypes) .. offsetof(iobupdate)+4)`.
8. `numbertypes()` ([`91-120`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L91-L120)): only for wearos-flagged hosts (librenums and Nightnums ranges). Not relevant for phones.
9. **`suptodate`** (u32 16, no ack) ([`236`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L236)).
10. If `resetdevices`: **`sresetdevices`** (no ack), then `resetdevices = false`.
11. Back in `doupdates`, if anything was sent (ret & 1): **`srender{type = wakeall (8)}`** with ack ([`datbackup.hpp:1399-1404`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1399-L1404)).

### 4.2 `starttime > 1` (new target with "send from date")
Before step 3 of §4.1:
* if sendnums and both lastlastpos are 0: `numsbackupsendinit(starttime)` ([`numdata.cpp:115-121`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.cpp#L115-L121) → [`numdata.hpp:1267-1310`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1267-L1310)). For each db with `firstpos != lastpos`, X sends **`sasklastnum{dbase}`**, reads R's `i32 pos`, clamps it, moves it back to the first entry with `time >= starttime`, and sets `changed[0].start = lastlastpos = pos` and `lastmeal`. **No `snuminit` is sent** unless `pos <= firstpos`, in which case it falls back to `sendbackupinit`.
  Then `startSendCalibrate = firstafter(starttime)` and `updateCaliTime(ind,starttime)` for those sensors ([`datbackup.cpp:149-159`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L149-L159)).
* if sendstream or sendscans: `Sensoren::setbackuptime()` ([`sensoren.hpp:1558-1575`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1558-L1575)) walks sensors from last down to 0. For each one, `SensorGlucoseData::setbackuptime()` ([`SensorGlucoseData.hpp:2081-2140`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2081-L2140)) sends **`saskfile{name="sensors/<name>/info.dat", off=0, len=80 (=offsetof(pollinterval))}`** and reads R's copy of the header. When R has at least 76 bytes, X initialises `update[ind].scanstart`, `histstart` and `streamstart` (only where they are still 0) from R's `lastscantime/scancount/endhistory` and from X's own data searched by time. `starttimeindex` records the progress (a resumable loop).

### 4.3 Reset by `-Z n` or by R's "new connection" magic (`resethost` → `clearhost`)
`clearhost(ind)` ([`datbackup.hpp:556-586`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L556-L586)):
* shuts down the sender socket;
* `sensors->updateinit(ind)`: zeroes `Info.update[ind]` and `calis[0..1].caliUpdated[ind]` in **every** sensor's info.dat ([`sensoren.hpp:1551-1556`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1551-L1556), [`SensorGlucoseData.hpp:1947-1951`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1947-L1951));
* zeroes `startsensors, firstsensor, updatesettings, startSendCalibrate, backupupdated, iobupdated`;
* sets **`starttime = 0`**, `starttimeindex = 0xFFFF`, and clears `resetdevices, sendjugglucoid, blueWatch, sendNight, sendLibre`;
* `nums[0..1].clear()` (`len=1, changed[0]={0,0}, lastmeal=0, lastlastpos=0`) ([`datbackup.hpp:103-108`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L103-L108)).

The `-Z` option is at [`cmdline/main.cpp:452-456`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L452-L456) and [`740-745`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L740-L745); the magic path at [`backup.cpp:325-330`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L325-L330).

Result: the next `wakeall` resends **everything from position 0**: all nums spans from index 0, all meals, all calibrations, all polls/scans/history and the whole sensors.dat, settings and iob. **No `snuminit` is sent** in this case, because `starttime == 0`.

---

## 5. Incremental updates

### 5.1 Sender thread and wake bits
`backupthread()` ([`datbackup.hpp:1249-1301`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1249-L1301)):
* It waits on its `condvar_t` (`lockwait`, [`1406-1421`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1406-L1421)), then takes `current = dobackup` and sets `dobackup = 0`.
* If `current == wakestream` exactly, `countSync++` (a 4-bit counter in updateone). Every 5th such wake is upgraded to `wakeall` ([`1294-1298`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1294-L1298)), so a full update still happens periodically.
* It then calls `doupdates(current, ind)` ([`1391-1405`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1391-L1405)). That function is a short-circuit `&&` chain. Each step runs only if its bit is set, and the chain stops at the first failure (return 0):
  1. `wakenums (4)` → `updatenums()`
  2. `wakestream (1)` → `updatestreamu()`
  3. `wakescan (2)` → `updatescansu()`
  4. `wakeUpSwitch (1024)` → `updateBeforeSwitch()`
  5. `wakeall (8)` → `update()`

  Then it sends `srender{type = OR of the bits whose step returned odd (1 = "sent something")}`.
* Wake-bit values ([`datbackup.hpp:223-233`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L223-L233)): stream 1, scan 2, nums 4, all 8, stop 16, other 32, end 64, send 128, reconnect 256, streamsend 512, UpSwitch 1024. Bits 16 and up only control the thread itself. Bits 128, 256 and 512 matter only for active targets.
* `condvar_t::wakebackup` ORs the new bits into `dobackup` and only notifies if it was 0 before ([`datbackup.hpp:260-273`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L260-L273)), so wakes are coalesced.
* Each of `updatenums/updatestreamu/updatescansu` first checks `if(starttime) return update();` ([`datbackup.cpp:254-256`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L254-L256), [`280-282`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L280-L282), [`291-293`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L291-L293)). A pending initial sync always runs before any incremental step.
* All three steps return 2 (meaning "skipped, OK") when their `send*` flag is off for this target.

### 5.2 How X is woken when data arrives from S (or is produced locally)
| Trigger on X | Wake | Ref |
|---|---|---|
| S's `srender{type}` arrives on X's receiver connection | `backup->wakebackup(type)`, or `wakeall` when type is 0. **This is the main mechanism**: S sends srender with the bits of what it just sent, and X forwards with the same bits. | [`getcommand.cpp:269-284`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L269-L284) |
| `snums` from S → `backupnums()` | `updateposnowake(off,off+nr)` for every target, then `wakebackup(wakenums)` | [`numdata.hpp:1422-1471`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1422-L1471) |
| `sbackup` / `sbackupstop` from S | `wakeupall()` → `wakebackup(wakeall\|wakereconnect)` | [`getcommand.cpp:304-315`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L304-L315), [`datbackup.cpp:311-316`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L311-L316) |
| `swakeupstream` from S | `wakebackup(wakestream\|wakereconnect)` | [`getcommand.cpp:316-326`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L316-L326), [`datbackup.cpp:317-322`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L317-L322) |
| local edits to nums (`numsave`, `numremove`, `updatepos`) | `wakebackup(wakenums)` | [`numdata.hpp:635-641`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L635-L641), [`1160-1169`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1160-L1169) |
| server start (`main`) and SIGUSR1 | `wakesender()` + `wakebackup(wakeall)` | [`cmdline/main.cpp:845-849`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L845-L849), [`879-883`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L879-L883) |
| passive connect | `wakebackup(wakeall)` for that target only | [`datbackup.cpp:709`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L709) |

`Backup::wakebackup` skips network-only targets while `networkpresent == false` ([`datbackup.hpp:1443-1494`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1443-L1494)). The server sets `networkpresent = true` ([`main.cpp:866`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L866)).

Side effects on per-receiver state when X receives from S, applied to **all** send targets:
* `sfileonce info.dat` with `0x8000|i, streamstart` → `processglucosevalue()` → `backstream(streamstart)`: sets `changedstreamstart = true` and `update[k].streamstart = min(update[k].streamstart, streamstart)` ([`main.cpp:892-926`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L892-L926), [`glucose.cpp:46-57`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L46-L57)). A finished sensor that receives fresh stream is un-finished and `resensordata(i)` lowers `startsensors` and `firstsensor` ([`datbackup.hpp:355-367`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L355-L367)).
* `0x4000|i, histstart` → `backhistory`: `changedhistorystart = true`, `histstart = min(...)` ([`glucose.cpp:26-36`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L26-L36), [`518-521`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L518-L521)).
* `0xC000|i, CaliUpdateFrom` → `backcalibrated` lowers `calis[h].caliUpdated[k]`, and `setCalibrates(i)` lowers `startSendCalibrate` for targets with sendnums ([`glucose.cpp:37-45`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L37-L45), [`524-530`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L524-L530); [`datbackup.cpp:838-846`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L838-L846)).
* `snums` → `updateposnowake` (§5.3). `snumnr` → only `setlastpos` (the change reaches R because `lastlastpos != lastpos`). `snuminit` → only `setfirstpos`; it is **not** propagated to existing targets.
* `sresetdevices` from S → `updateDevices()`: `resendResetDevices()` (sets `resetdevices = true` on targets with sendscans) plus `sendstartsensors(last)` ([`getcommand.cpp:113-123`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L113-L123), [`datbackup.hpp:370-380`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L370-L380)).
* `meals.dat` from S: S writes `gotlastmeal := startmeal` into X's meals.dat (it is one of the segments). X's `updatemeal` uses `min(lastmeal, gotlastmeal)` and afterwards sets `gotlastmeal = UINT32_MAX` ([`Meal.hpp:133-156`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L133-L156)). **Quirk:** only the first target to update after the change sees the rewind; other targets only get meals with index >= their own `lastmeal`.
* New sensor or sensors.dat change from S: there is no hook. It is detected because `last()` (read from the received sensors.dat header) is now >= `startsensors`.

### 5.3 Nums (`Numdata::update`, [`numdata.hpp:1327-1414`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1327-L1414); changed spans [`1110-1159`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1110-L1159))
Per target and per nums db there is a `changednums` ([`datbackup.hpp:97-109`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L97-L109)): `len`, `changed[100]` of `{start,end}`, `lastmeal`, `lastlastpos`.
* `changed[0] = {start, end=0}` means "from start up to the current lastpos". `changed[1..len-1]` are explicit `[start,end)` spans.
* `updateposnowake(pos,end)`:
  * if `pos >= changed[0].start`, nothing is needed;
  * if `pos == changed[0].start-1`, extend `changed[0]`;
  * otherwise append `{pos,end}` (merging with overlapping spans);
  * if there are more than 100 spans, collapse everything into `changed[0].start = min`.
* On update: if no span is non-empty, send `snumnr{dbase,lastpos}` only when `lastlastpos != lastpos`. Otherwise send **one `snums`** with all non-empty spans, emitted from the last array entry down to `changed[0]`, and `last = lastpos`. After success: `lastlastpos = lastpos`, `changed[0] = {lastpos,0}`, `len = 1`. If new changes arrived during the send (`updatebusy`), the spans are kept and 2 is returned.
* `dbase` on the wire = `(bool)ident` ("here" = 0, "watch" = 1). The `changednums` slot is the **opposite**: `getindex()` returns 1 for ident 0 ([`numdata.hpp:1077-1082`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1077-L1082)). Meals use slot 1's `lastmeal`.
* `Num` = 16 bytes `{u32 time; u32 mealptr|librenr|calibration:31+exclude:1; float value; u32 type}` ([`nums/num.h`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/num.h)).
* Deletions:
  * Deleting the last entry or shifting entries down lowers lastpos, and the moved range is resent ([`numdata.hpp:723-751`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L723-L751)).
  * Deleting in the middle sets bit 31 of `type`, copies `time` from the previous entry, and resends that one entry ([`682-700`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L682-L700)).
  * Truncation is carried by `last`/`snumnr` being smaller than before.
* Positions are element indices relative to the nums data start (byte 256 of the `nums*` file).

### 5.4 Meals (`mealdata::updatemeal`, [`meal/Meal.hpp:133-156`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L133-L156))
If `mealindex > startmeal` (with `startmeal = min(lastmeal, gotlastmeal)`), send one sfileonce `meals.dat` with five segments:
1. `[8,4] = startmeal` (written into R's `gotlastmeal`)
2. `[0,6]`: unitnr, reserved, ingredientnr
3. `[12, 4+unitnr*20]`: mealindex and the used units
4. `[816, ingredientnr*52]`: ingredients
5. `[22136+startmeal*8, (mealindex-startmeal)*8]`: meal elements

Then `lastmeal = mealindex`. The `mealdata` layout is at [`Meal.hpp:40-55`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/meal/Meal.hpp#L40-L55). My hand-computed offsets: unitnr 0, ingredientnr 4, gotlastingredient 6, gotlastmeal 8, mealindex 12, units 16 (40×20), ingredients 816 (410×52), themeals 22136 (8 bytes each).

### 5.5 Stream (`SensorGlucoseData::updatestream`, [`SensorGlucoseData.hpp:2262-2446`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2262-L2446))
Incremental (`wakestream`): `Sensoren::updatestreams()` iterates from `last()` **down to** `firstsensor`. It stops at the first sensor whose sensors.dat `maxtime() < now` and skips sensors marked finished ([`sensoren.hpp:1746-1778`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1746-L1778)). It calls `updatestream(..., sendscan = sendscans ? 2 : 1)`. In the full update, sendscan = 0.

For each sensor, with `streamstart = update[ind].streamstart` and `streamend = Info.pollcount` (a snapshot):
1. If `streamstart < streamend`:
   1. sfileonce `sensors/<n>/polls.dat`, offset `streamstart*20`, the ScanData records `[streamstart, streamend)` ([`2285`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2285)).
   2. History piggyback:
      * sendscan 1: `newsendhistory(sendStream=true, histend=endStreamhistory)`. Only data.dat elements with the "streamed" flag (`glu[2] & 0x4000`) are sent, as several segments ([`glucose.cpp:102-144`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L102-L144)).
      * sendscan 2: `oldsendhistory(sendinfo=false, endStreamhistory)` sends the contiguous range `[histstart-1 (if >0), endStreamhistory+1)` ([`glucose.cpp:81-101`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L81-L101)).
   3. sfileonce `sensors/<n>/info.dat` with **dowith `0x8000|i`, trailer `i32 streamstart`** (the old start), containing these segments:
      * `[76,17]` raw bytes pollcount..streamingIsEnabled (snapshot);
      * type-specific segments (Sibionics/AidexX/AccuChek/Air/Dexcom, sent once per target while `!update[ind].siStream`; see the table in §7.2);
      * non-Sibionics/non-AidexX: `[438,4]` endStreamhistory and startedwithStreamhistory, if history was written;
      * `[8,4]` starthistory, if `sendhiststart`;
      * `[2366,2]` broadcastfrom.
   4. If start-time info was sent (`updateStarttime`): sfileonce `sensors/sensors.dat [(i+1)*32, 4]` = the sensor record's starttime ([`sensoren.hpp:1582-1592`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1582-L1592)). Then `siStream = true`.
   5. `streamstart = streamend`, unless `changedstreamstart` was set meanwhile. Clear `sendhiststart`.
   6. Libre 3 / Dexcom / Air: `oldsendhistory(sendinfo=true, endhistory)` sends data.dat, then sfileonce info.dat `[12,4]=endhistory`, `[32,4]` lastLifeCountReceived (and lastHistoricLifeCountReceivedPos) with **dowith `0x4000|i`, trailer `i32 histstart`** ([`glucose.cpp:65-80`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L65-L80)).
2. Otherwise, if `sendbluetoothOn`: sfileonce info.dat `[92,1]`.

Return values: 1 = sent something, 2 = nothing to do, 0 = failure.

### 5.6 Scans and history (`SensorGlucoseData::updatescan`, [`glucose.cpp:161-423`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L161-L423); `wakescan` uses `updatescanalg`, i.e. `dotoch=false`)
* **Dexcom / Sibionics / AccuChek / Air / AidexX**: once per target (`!update[ind].siScan`) send the identification segments of info.dat (table in §7.2), set `siScan` and return 5. Otherwise return 5 if `sendstreaming` was set, else 2. A return of 5 makes `Sensoren::update` resend sensors.dat from this sensor and send `sresetdevices`.
* **Libre 2 / Libre 3** (generic):
  1. History:
     * not Libre 3 and sendstream: `oldsendhistory(false, max(endStreamhistory, endhistory))`;
     * not Libre 3 and no sendstream: `newsendhistory(false, endhistory)` (only *non*-streamed elements);
     * Libre 3: `updateKAuth` instead, which sends info.dat `[280,150]` if haskAuth, else `[429,1]`, when `sendKAuth` is set.
  2. If `scancount > update[ind].scanstart`: sfileonce `current.dat` (`scanstart*20`, ScanData[scanstart..scancount)), then sfileonce `trends.dat` (`scanstart*32`, 32-byte records), then `scanstart = scancount`.
  3. If anything was sent, or `sendstreaming`, or `dotoch` (a new sensor in a full update): sfileonce info.dat with:
     * `[0,76]` header snapshot;
     * `[92,22]` streamingIsEnabled, patchState, manualwarmup, flags and deviceaddress[18];
     * `[438,4]`, if the stream history was written.
  4. Then, if `sensors/<n>/state.lnk` exists on X: sfileonce of the state file it names, plus sfileonce `sensors/<n>/state.lnk` with the link text ([`glucose.cpp:397-415`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L397-L415)). Libre 2 only in practice.
  5. Returns 5 if `sendstreaming` was set, else 1.

### 5.7 Calibrations (`updateCali`, [`SensorGlucoseData.hpp:2574-2608`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2574-L2608))
For table h (0 = stream, 1 = history), `nr = calis[h].caliNr` and `from = calis[h].caliUpdated[ind]`. If `from != nr`, send sfileonce info.dat:
* `[2376+1240h+24·from, 24·(nr-from)]` CaliPara entries, only if `from < nr`;
* `[3576+1240h, 4] = nr`.

The dowith is **`0xC000|sensorindex`** and the trailer is the 5-byte `CaliUpdateFrom{from, h}`. Then `caliUpdated[ind] = nr`. This step runs only inside `updatenums` (wakenums or a full update) and only if `sendnums` is set.

### 5.8 Other files
* Sibionics: `sensors/<n>/state.bin` (the whole file, offset 0) when `update[ind].rawstreamstart < Info.lockcount`. Then `rawstreamstart = lockcount` ([`SensorGlucoseData.hpp:2231-2241`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2231-L2241)).
* Air:
  * the `sensors/<n>/sensorInfo` file, together with the starttime, the first time stream is sent ([`2370-2380`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2370-L2380));
  * `sensors/<n>/generated` when `rawstreamstart < pollcount`, but only on the `wakeUpSwitch` path ([`2242-2260`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2242-L2260)).
* `wakeUpSwitch` (`updateBeforeSwitch`, [`datbackup.cpp:299-310`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L299-L310)): if the target has no sendstream, this immediately sends `srender{wakeUpSwitch|wakeall}`. Otherwise it runs the Air "generated" logic for active sensors.

---

## 6. Per-receiver state that must be kept (and where Juggluco persists it)

### 6.1 `backup.dat` (mmap of `struct updatedata`, [`datbackup.hpp:234-248`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L234-L248); file name at [`111`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L111))
`updatedata` contains: `i32 hostnr; u32 receive; char port[6]; bool hasrestore; u8 sendnr; passhost_t allhosts[8]; updateone tosend[6]; u32 lastused[8]; i32 NRturnserver; TurnServer turnserver[4]`.

`updateone` ([`datbackup.hpp:119-147`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L119-L147)), my offsets, 1732 bytes in total:

| off | field | meaning for sending |
|---|---|---|
| 0 | i32 ind | sendindex |
| 4 | i32 allindex | host index |
| 8 | i32 startsensors | sensors.dat must be resent from this sensor index (lowered by `sendstartsensors`, [`datbackup.cpp:824-833`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L824-L833)) |
| 12 | u8 backupupdated | unused |
| 13 | countSync:4 | counts pure stream wakes (every 5th becomes wakeall) |
| 14 | u16 iobupdated | last `Tings.iobupdate` sent |
| 16 | u32 updatesettings | last `Tings.update` counter sent |
| 20 | i32 firstsensor | first sensor still considered active |
| 24 | u32 starttime | 0 = initialised; 1 = full init pending; >1 = init from this unix time pending |
| 28 | u16 starttimeindex | resume point of the `setbackuptime` walk |
| 30 | bool resetdevices | `sresetdevices` pending |
| 32/33/34 | bool sendnums / sendstream / sendscans | what this target receives |
| 35 | restore:4, blueWatch:1, sendNight:1, sendLibre:1, sendjugglucoid:1 | |
| 36 | u16 startSendCalibrate | first sensor whose calibrations may need sending |
| 38 | u8 NotUsed[14] | |
| 52 | changednums nums[2] (840 bytes each) | `{i32 len; {i32 start,end}[100]; u32 lastmeal; u8 reserved[28]; u32 lastlastpos}` |

A reimplementation **does not need to be byte-compatible with backup.dat**, which is internal to X. It must keep the same semantic fields.

### 6.2 Per receiver and per sensor: inside each sensor's `info.dat` on X
`Info.update[8]`, at offset 120, 20 bytes each ([`SensorGlucoseData.hpp:374-384`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L374-L384), [`483`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L483)):
`u32 scanstart, histstart, streamstart, rawstreamstart`, then a flags byte at +16: bit 0 sendbluetoothOn, 1 sendstreaming, 2 changedstreamstart, 3 changedhistorystart, 4 sendKAuth, 5 sendhiststart, 6 siScan, 7 siStream.

Also `calis[h].caliUpdated[8]` at `3580+1240h` (u32 each).

This region is never part of any segment that S sends, so receiving from S does not overwrite it. A reimplementation should store these per (receiver, sensor) values outside the file, for example in a DB table.

### 6.3 Global flags that fan out to all receivers
`setsendstreaming`, `setsendKAuth`, `sendbluetoothOn`, `setsendhiststart` and `setsiScan` set the corresponding flag for every target index below `sendnr` ([`SensorGlucoseData.hpp:2172-2207`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2172-L2207)). On a pure relay most of these are never set. They come from local sensor handling, which a relay does not do. **Uncertain:** see §9.

### 6.4 Reset rules
* `clearhost`: see §4.3.
* New configuration: `changereceiver` (starttime 1 or T).
* R's magic last byte 0 means R considers this a new connection, which triggers a reset.
* X's own `newconnection` flag makes X answer with magic last byte 0 ([`backup.cpp:314-322`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L314-L322)), so R resets its side.

---

## 7. What the sender must interpret inside the data files

### 7.1 `sensors/sensors.dat`
32-byte records.

Record 0 is the info block `{i32 last, current, markold, start, version}` ([`sensoren.hpp:105-107`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L105-L107)). The sender needs **`last`** (index of the last sensor). It sends a snapshot of that value in segment [0,4].

Records 1..last+1 hold `sensor` ([`sensoren.hpp:48-93`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L48-L93)):

| off | field |
|---|---|
| 0 | u32 starttime |
| 4 | u32 endtime |
| 8 | char name[17] (16 chars + NUL; the directory name under `sensors/`) |
| 25 | u8 present |
| 26 | finished:7, initialized:1 |
| 27 | u8 halfdays |
| 28 | u16 next |
| 30 | u16 prev |

The sender needs:
* `name`, to build paths;
* `finished`, to skip finished sensors;
* `starttime` and `halfdays`, for `maxtime() = starttime + (halfdays>=48 ? 572h : (halfdays?halfdays:29)*12h)`. This is used to stop the incremental stream/scan loops and to compute `firstsensor`.

### 7.2 `sensors/<name>/info.dat` (`struct Info`, [`SensorGlucoseData.hpp:386-590`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L386-L590))
Hand-computed offsets; **verify on a real file** (sizeof is expected to be 4864).

| off | size | field | used by the sender for |
|---|---|---|---|
| 0 | 4 | starttime | segment; sensor-type extras |
| 4 | 4 | lastscantime | time-based init (from R's copy) |
| 8 | 4 | starthistory | segment when sendhiststart |
| 12 | 4 | endhistory ("scan" history end, one past last) | **history end counter** |
| 16 | 4 | scancount | **scan counter** (current.dat and trends.dat) |
| 20 | 2 | startid | |
| 22 | 2 | interval (s; 300 means Libre 3) | type detection, history position |
| 24 | 2 | pollstart | |
| 26 | 1 | dupl:7 \| **dexcom** bit7 | `elsize = 4 + (dupl+1)*2` bytes per data.dat element; type |
| 27 | 1 | days:7 \| **sibionics** bit7 | type; validity (10..46) |
| 28 | 4 | pin / warmup, wearduration | |
| 32 | 2 | lastLifeCountReceived | segment `[32,4]` |
| 34 | 2 | lastHistoricLifeCountReceivedPos | |
| 36 | 12 | union ident / wearduration2 / askEarlier | |
| 48 | 28 | union: Libre 2 info+blueinfo / Sibionics (siBlueToothNum@48, bits@57, siDeviceNamelen@58, siToken@59, siDeviceName@60) / Dexcom (sharedKey@48, DexDeviceName@64) | type extras |
| 76 | 4 | **pollcount** | **stream counter** (polls.dat) |
| 80 | 8 | double pollinterval | |
| 88 | 4 | lockcount | Sibionics state.bin counter |
| 92 | 1 | streamingIsEnabled | |
| 93 | 1 | patchState | |
| 94 | 1 | manualwarmup | |
| 95 | 1 | bit4 isAidexX, bit5 air, bit6 accuChek, bit7 auth12 | **sensor type** |
| 96 | 18 | deviceaddress | |
| 114 | 2 | libreviewScan | |
| 116..119 | | misc flags | |
| 120 | 160 | **update[8]** (per receiver, §6.2) | sender state |
| 280 | 149 | union kAuth / airData / {siIdlen u32 @280, siId[68] @284} / aidexXdat (iv@280, serialnr@358, scanlen@359) | type extras |
| 429 | 1 | haskAuth | |
| 438 | 2 | **endStreamhistory** | **stream-history end** |
| 440 | 2 | startedwithStreamhistory | |
| 444 | 1920 | sendLibre[] | (not sent) |
| 2366 | 2 | broadcastfrom | segment |
| 2375 | 1 | warmupstartpos | type extras |
| 2376 | 1240 | calis[0] {CaliPara[50] (24 bytes each), u32 caliNr @+1200, u32 caliUpdated[8] @+1204, u32 unused} | **calibration counter caliNr** |
| 3616 | 1240 | calis[1] | |

Type-specific identification segments sent once per target ([`glucose.cpp:162-292`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/glucose.cpp#L162-L292); [`SensorGlucoseData.hpp:2336-2395`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L2336-L2395)):

| Sensor type | Segments (offset, length) |
|---|---|
| Dexcom | `[0,34]`, `[280,72]`; stream extras `[0,4]`, `[64,12]`, `[96,18]` |
| Sibionics | `[0,28]`, `[48, 28 or 10]`, `[88,4]`, `[94,1]`, `[280,72]`, `[2375,1]`; stream extras `[0,4]`, `[57,19]`, `[96,18]` |
| AccuChek | `[0,34]`, `[88,4]`, `[94,2]`, `[280,72]`; stream extra `[0,4]` |
| Air | `[0,34]`, `[94,2]`, `[280,69]`; stream extras `sensorInfo` file + `[0,4]` |
| AidexX | `[0,34]`, `[94,2]`, `[280,16]`, `[358,2+scanlen]`, `[2375,1]`; stream extras `[0,4]`, `[93,1]` (if sendhiststart), `[32,2]` |

Type derivation:
* `isLibre3 = !air && !aidexX && !accuChek && !sibionics && !dexcom && interval==300`;
* `isLibre2 = none of those and interval != 300` ([`SensorGlucoseData.hpp:1190-1223`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1190-L1223)).

### 7.3 Element sizes
| File | Element |
|---|---|
| `polls.dat`, `current.dat` | `ScanData` = 20 bytes `{u32 t; i32 id; i32 g; i32 tr; float ch}` |
| `trends.dat` | 32 bytes (16×u16) |
| `data.dat` (history) | `elsize = 4 + (dupl+1)*2` (normally 12): `{u32 time; u16 id; u16 glu[dupl+1]}`. `glu[2] & 0x4000` = "streamed" flag, used by `newsendhistory`. Position = element index. |

### 7.4 Nums files (`nums/here/nums0`, `nums/watch/nums-1` …, [`numdata.hpp:72-88`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L72-L88))
Header of 256 bytes. The sender needs:
* i64 ident @32 (newident);
* i32 **lastpos @128**;
* i32 **firstpos @144**.

Data (`Num`, 16 bytes) starts at byte 256 and positions are relative to it. "Changed" information is not stored in the file. It is the per-target span list (§5.3), fed by received `snums`.

### 7.5 `settings.dat` (`struct Tings`, [`settings/settings.hpp:181-430`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.hpp#L181-L430))
Too large to hand-compute reliably. The sender needs the offsets of: `jugglucoID` (u64), `nochangenum` (bool), `bloodvar` (u8), the range `update` (u32 counter) … `mealvar` (i8), `gs3id[12]`, the range `insulintypes` … `iobupdate` (i32 counter), and for wearos only `librenums..libreaccountIDnum` and `Nightnums..nightinterval`.

**Practical approach:** S sends X exactly these segments, produced by the same `updateone::update` code on S. Record the (offset, length) of each `settings.dat` segment X receives and replay them. The 4-byte `update` counter is the first 4 bytes of the segment that starts at `offsetof(Tings,update)`. `iobupdate` is the last 4 bytes of the insulin segment. Alternatively, compile a one-line `offsetof` probe against [`settings.hpp`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/settings/settings.hpp) (no compiler was available here).

### 7.6 Opaque vs interpreted (summary)
* **Opaque** (stored and forwarded as bytes): all payload bytes of polls, current, trends, data, state files, state.bin, sensorInfo, generated; most of info.dat; meals ingredients and units; Num records; settings ranges.
* **Must be interpreted:**
  * sensors.dat `last` and the per-sensor `name / finished / starttime / halfdays`;
  * info.dat counters `pollcount(76)`, `scancount(16)`, `endhistory(12)`, `endStreamhistory(438)`, `starthistory(8)`, `lockcount(88)`, `calis[h].caliNr`, `haskAuth(429)`, `dupl(26)`, `interval(22)` and the type bits (26 bit7, 27 bit7, 95 bits 4-6);
  * nums `lastpos`, `firstpos`, `ident`;
  * meals `unitnr`, `ingredientnr`, `gotlastmeal`, `mealindex`;
  * settings `update` and `iobupdate`;
  * the dowith trailers received from S (§5.2).

---

## 8. Reimplementation notes
* Keep one ordered, stop-and-wait command stream per receiver. Every ack command blocks until the 4-byte plaintext ack `11 00 0E 00` arrives. Send each frame with one write.
* Simplest compatible model:
  1. Store S's files exactly as received (paths relative to the data dir).
  2. Keep, per (receiver, sensor), `streamstart / histstart / scanstart / rawstreamstart / caliUpdated[2]` plus flags; per receiver, the `updateone` fields; per (receiver, db), the span list and `lastlastpos/lastmeal`.
  3. On every relevant event from S, apply the "lower the start" rules of §5.2.
  4. Wake the receiver's sender with the same bits S put in its `srender`.
* On wakes, follow §5.1. On connect, run §4.1 (or §4.3 after a reset).
* The **critical notification** for R is the `info.dat` sfileonce with dowith `0x8000|sensorindex` and trailer `i32 streamstart`, sent after the `polls.dat` data. Without it R stores the data but does not call `processglucosevalue` (no display or alarm refresh). Also send `srender` at the end of a batch, which makes R call `render()`.
* Order matters on R: send the data files first (polls.dat, data.dat), then info.dat, because R reads its info counters when processing the dowith.

---

## 9. Uncertain / needs testing against the real app
1. **All struct offsets in §6.1, §7.2 and §5.4 were computed by hand** (LP64 GCC/Clang, little-endian bit-fields). Verify against real files, e.g. `info.dat` should be 4864 bytes and `pollcount` should equal the polls.dat record count, or verify with an `offsetof` probe build. On 32-bit x86 Android, `double` alignment inside structs differs; arm32 should match.
2. `Tings` offsets were not computed (§7.5). The "learn from S" approach assumes S is configured to send nums and scans to X; otherwise some segments are never seen.
3. The extra `ant` byte in serverloop ([`backup.cpp:579-586`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L579-L586)). If X both receives from R and sends to R without `sendall`, X reads 1 byte that R sends only when R itself sends to X. Behaviour with mixed configurations needs a test (it could swallow the first nonce byte).
4. The Ascon context continues across frames and interleaves enc/dec on one context per socket (modified LibAscon). The exact byte-level behaviour of saskfile/sasklastnum replies in encrypted mode must be validated with a real phone.
5. Whether R really needs `srender`, or `suptodate`, after a batch; whether R tolerates `snums` with `first` uninitialised; padding bytes that are garbage rather than zero.
6. `-Z`/`resethost` resends from 0 **without `snuminit`**. Check that the phone accepts nums from position 0 when its firstpos differs.
7. Sensor sends abort when `getSensorData(i)` returns NULL for any i in `[firstsensor, last]` ([`sensoren.hpp:1689-1690`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/sensoren.hpp#L1689-L1690)). One corrupt info.dat on X blocks all full updates. Confirm, and decide whether to replicate this.
8. The meals `gotlastmeal` quirk with multiple receivers (§5.2). Old-meal edits may reach only one receiver.
9. Per-target flags normally set by local sensor processing (`sendstreaming`, `sendKAuth`, `sendhiststart`, `sendbluetoothOn`, `siScan` reset) are never set on a relay. Check whether R depends on them, e.g. Libre 3 kAuth or Sibionics re-sends after a reset.
10. History for **Libre 2 stream-only targets** is only sent incrementally (streamed elements only, `newsendhistory`), never in the full update (sendscan=0). Check whether that is acceptable for R.
11. `oldsendhistory` sends `histstart-1 .. histend` inclusive, one element beyond the end. Check that it is harmless on R.
12. Behaviour when R's sensor indices differ from S's (for example R has its own sensors). Juggluco assumes identical `sensors.dat` on all sides; mixed setups are untested here.
13. `shouldaskfordata` / `getshouldaskfordata()` (true if any host has `receivefrom==3`, [`datbackup.hpp:287-296`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L287-L296)) is computed but **never used** in this code base. It is probably used by the Android Java layer to ask for data on start. It does not affect the send side.
14. `restore` and `hasrestore` are stored but unused on the send path here.
