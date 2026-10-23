# Juggluco mirror protocol (TCP): connection setup, framing, encryption

Part 1 of the reimplementation spec. Source references point to Juggluco at commit
[43fdbe1](https://github.com/j-kaltes/Juggluco/tree/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp)
(11.0.x); all paths below are relative to the directory `Common/src/main/cpp`. In
`net/Connect.hpp` the file starts with an extra header, so the line numbers quoted for it are
real file line numbers.

Conventions:
* All multi-byte integers on the wire are in **native little-endian** order (raw C structs
  copied with `memcpy`/`send`, no `htonl`). Android ARM/x86 and Linux x86 are all LE.
  Structs use natural alignment and are not `packed` ([`net/comtypes.hpp:83-176`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L83-L176)).
* "Connector" means the side that calls `connect()`. "Acceptor" means the side in `accept()`.
  The acceptor is the Juggluco server/receiver listening on the mirror port.
* Default mirror port: the app UI and the help text use **8795** ([`cmdline/main.cpp:156`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L156)). The
  compiled-in fallback `defaultport` is `MIRRORPORT` or "9996" ([`datbackup.hpp:53-59`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L53-L59)), so the
  real app build probably defines `MIRRORPORT=8795`. The port is just configuration.

---------------------------------------------------------------------------------------------

## 0. Quick reference (byte sequence on one TCP connection)

```
Connector                                   Acceptor
---------                                   --------
[label: 16 bytes]           ----------->    (read only if no unnamed host matched by IP; §1.4)
sendmagic: 14 bytes         ----------->    (one recv() must return exactly 14 bytes)
                            <-----------    receivemagic: 15 bytes
[type: 1 byte]              ----------->    (sent if stype!=0; read only in some cases; §1.5)
--- if a password is set ---
nonceA: 8 random bytes      ----------->
                            <-----------    nonceB: 8 random bytes
key = 16-byte derived password, nonce = nonceA||nonceB, Ascon-128a init (one ctx per connection)
--- then the data phase ---
DATA SENDER --> DATA RECEIVER:  frames (encrypted §3, or plain §2)
DATA RECEIVER --> DATA SENDER:  4-byte plain ack 0x000E0011 (LE: 11 00 0E 00) for each sack
                                command, plus encrypted replies to sasklastnum/saskfile/sopen
```

Which side is the *data sender* does not depend on who connected. It depends on the host
configuration and on the optional type byte (§1.5).

---------------------------------------------------------------------------------------------

## 1. Connection setup

### 1.1 Roles and threads

| Scenario | Connector (TCP client) | Acceptor (TCP server) | Data flows |
|---|---|---|---|
| A. Phone **sends** to a passive server | phone sender thread: `updateone::open()` -> `Connect::makeconnection(host, crypt, saysender(host))` ([`datbackup.cpp:361-392`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L361-L392), [`net/sender.cpp:713-722`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L713-L722)) | server `serverloop()` -> `testreceivemagic` -> `receiverthread` -> `getcommands` ([`net/backup.cpp:431-611`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L431-L611), [`410-428`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L410-L428), [`net/getcommand.cpp:737-744`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L737-L744)) | phone -> server |
| B. Phone **receives** from a passive server ("active only" on the phone, "passive only" send on the server) | phone `activereceivethread()` -> `makeconnection(pass, ctx, sayactivereceive(pass))` then `activegetcommands()` ([`datbackup.cpp:442-566`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L442-L566), [`net/getcommand.cpp:745-752`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L745-L752)) | server `serverloop()` -> `hit->sendpassive` -> `TCPConnect::passivesender()` ([`net/backup.cpp:575-593`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L575-L593), [`datbackup.cpp:679-714`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L679-L714)); the server's sender thread then writes on this socket | server -> phone |
| C. (other direction, for completeness) Server is an active sender | server sender thread connects to the phone | phone listens | server -> phone |
| D. "Wake-up" ping (`sendup`) | side that has `receivefrom==3 && index<0` connects, sends one `sbackup` (cmd 5) command, then closes | the other side's `interpret()` calls `wakeupall()` -> it reconnects actively | ([`datbackup.cpp:397-435`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L397-L435), [`net/getcommand.cpp:304-315`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L304-L315), [`datbackup.cpp:608-617`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L608-L617)) |

The type byte (`stype`) chosen by the connector:
* `saysender(host)` (sender connections) is `RECEIVEFROM`=2 when `host->activereceive`, otherwise 0
  ([`datbackup.cpp:331-337`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L331-L337)).
* `sayactivereceive(host)` (active-receive connections) is `SENDPASSIVE`=1 when `host->index>=0`
  (this host is also a send target), otherwise 0 ([`datbackup.cpp:342-347`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L342-L347)).
* The constants are `SENDPASSIVE 1` and `RECEIVEFROM 2` ([`datbackup.cpp:331-332`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L331-L332), [`net/backup.cpp:571-572`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L571-L572)).

### 1.2 Connector side, byte by byte

From `Connect::shakehands` ([`net/sender.cpp:235-264`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L235-L264)), `testsendmagic` ([`net/sender.cpp:132-181`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L132-L181))
and `makeconnection` ([`net/sender.cpp:713-722`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L713-L722)):

1. TCP connect. Non-blocking connects go to every configured IP, or to every DNS result when a
   hostname is set, in parallel. `poll` waits up to 60 s ([`net/sender.cpp:282-330`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L282-L330), [`333-672`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L333-L672),
   [`TCPConnect.hpp:54-56`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp#L54-L56)). After connecting, `SO_RCVTIMEO` and `SO_SNDTIMEO` are 180 s
   ([`net/sender.cpp:227-233`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L227-L233), [`317`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L317), [`363`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L363), [`631`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L631)).
2. **Label** (only if the connector's host entry `hasname`): exactly 16 bytes, `passhost_t::getname()`
   ([`net/sender.cpp:237-247`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L237-L247)). Encoding ([`net/passhost.hpp:185-199`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L185-L199)): label characters up to 16;
   if the label is shorter than 16, the rest is zero-filled (the NUL included). A label of 16 or
   more characters is truncated to 16 bytes with no NUL.
3. **sendmagic**, 14 bytes ([`net/sender.cpp:144`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L144)):
   * bytes 0..9 are constant: `DA AD BE ED DE ED BE EF D1 EF` ([`net/sendmagicinit.h:22`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendmagicinit.h#L22)).
   * bytes 10..13 are random and generated **once per process** ([`net/sender.cpp:120-129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L120-L129)). Byte 13
     is forced non-zero (a loop until it is non-zero).
   * **Exception:** if `pass->receivedatafrom() && pass->newconnection`, byte 13 is sent as `00`
     ([`net/sender.cpp:136-143`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L136-L143)). This means "I am new / reset: start over".
4. Read exactly **15 bytes** (one `recv`, [`net/sender.cpp:154-165`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L154-L165)). Bytes 0..10 must equal
   `67 F9 2D 42 34 80 28 DE BA 81 3F` ([`net/receivemagic.h:22`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/receivemagic.h#L22)). The code only compares
   `recsize-4` = 11 bytes; the comment says "4 less for version info" ([`net/sender.cpp:167`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L167)).
   Bytes 11..13 are `00 00 00` and are not checked.
   * If byte 14 is `00`, the connector calls `resethost()`. That clears the send state for that
     host, so everything is re-sent from scratch ([`net/sender.cpp:175-178`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L175-L178), [`datbackup.hpp:693-699`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L693-L699)).
   * Then `newconnection=false` ([`net/sender.cpp:179`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L179)).
   * Errors: if the recv returns the wrong length, the error code is 2 and is reported as
     "Sender with label %s rejected by receiver". This happens when the acceptor closes because no
     host matched ([`net/sender.cpp:249-257`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L249-L257)). A wrong magic gives error 3.
5. **Type byte**: if `stype != 0`, send 1 byte with that value ([`net/sender.cpp:215-222`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L215-L222), [`258-260`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L258-L260)).
   It is sent without waiting.
6. If a password is set: run the **nonce exchange** in §3.2 ([`net/sender.cpp:68-88`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L68-L88)). The return
   value of `sendpassinit` is ignored by `makeconnection` ([`net/sender.cpp:719`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L719)).
7. Data phase. The connector's timeouts become recv 60 s and send 300 s
   ([`TCPConnect.hpp:32-38`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp#L32-L38), [`datbackup.cpp:386`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L386)). On the wake path the sender uses send 300 s and
   recv 0 ([`datbackup.cpp:418-422`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L418-L422)). The active receiver uses `receiversockopt` (§2.6,
   [`datbackup.cpp:546-549`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L546-L549)).

### 1.3 Acceptor side, byte by byte

`serverloop` ([`net/backup.cpp:431-611`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L431-L611)):

1. `accept()`. If `!networkpresent` the socket is closed at once ([`net/backup.cpp:464-468`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L464-L468)). Then
   `SO_RCVTIMEO` and `SO_SNDTIMEO` are set to 180 s ([`net/backup.cpp:469-475`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L469-L475)).
2. **Host matching** (§1.4). It may read the 16-byte label. If no host matches, the socket is
   closed and **nothing is sent** ([`net/backup.cpp:548-550`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L548-L550)).
3. `testreceivemagic` ([`net/backup.cpp:297-351`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L297-L351)):
   * ONE `recv(buf, 1024)` must return **exactly 14** bytes. Otherwise it fails
     ([`net/backup.cpp:304`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L304), [`346-349`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L346-L349)). This works because the connector waits for the reply after
     the magic.
   * Bytes 0..9 must equal `sendmagicspec[0..9]`, the constant above ([`net/backup.cpp:305-306`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L305-L306)).
   * If bytes 10..12 equal the acceptor's own random bytes 10..12, it reports "I don't connect
     with myself", sends no reply and fails ([`net/backup.cpp:307-310`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L307-L310)).
   * Reply: **15 bytes** `receivemagic` = `67 F9 2D 42 34 80 28 DE BA 81 3F 00 00 00 01`. The last
     byte is `00` instead when `pass->receivedatafrom() && pass->newconnection`
     ([`net/backup.cpp:312-323`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L312-L323)).
   * If the received byte 13 is `00`, the acceptor calls `resethost(*pass)`
     ([`net/backup.cpp:325-330`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L325-L330)). Then `newconnection=false` ([`net/backup.cpp:331`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L331)).
4. **Direction decision** ([`net/backup.cpp:575-600`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L575-L600)):
   ```
   if host.sendpassive:                    # I send to this host, passively only
       ant = SENDPASSIVE (1)
       if host.receivedatafrom() and not sendall(host):   # I also receive from it
           recv 1 byte into ant            # the type byte; on failure keep ant=1
       if ant == SENDPASSIVE:
           passivesender(): [receivepassinit if password] and use this socket as MY SENDER socket
           -> continue accept loop
       else:
           message "Other side should be active only"; FALL THROUGH to the receive path
   receive path: close the old receiver socket (shutdown, sleep 1 s, close), receiversockopt(),
       start receiverthread -> getcommands():
       password ? receivepassinit + encrypted loop : plain loop
   ```
   `sendall(host)` is true when this side sends nums, stream **and** scans to that host
   ([`datbackup.cpp:748-751`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L748-L751)).
   `passivesender` ([`datbackup.cpp:679-714`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L679-L714)) runs `receivepassinit(pass, crypts[host.index])`
   (§3.2), then moves the socket into the sender slot, sets sender timeouts and wakes the sender
   thread with `wakeall`.

### 1.4 Host matching on the acceptor (IP / label / noip / detect)

[`net/backup.cpp:477-551`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L477-L551). Hosts are the `passhost_t allhosts[]` ([`net/passhost.hpp:45-255`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L45-L255)).

Pass 1 (`find_if`, [`net/backup.cpp:479-491`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L479-L491)). For each host in order:
* if `host.hasname`, mark `hasname=true` and skip (named hosts are never matched by IP alone).
* else if `host.noip`, **match** (no IP test, no `passive()` test, no `deactivated` test).
* else if `host.passive() && host.hasip(peer)`, **match**.

`passive()` = `(receivefrom && !activereceive) || sendpassive` ([`net/passhost.hpp:168-170`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L168-L170)).

`hasip` ([`net/passhost.hpp:210-218`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L210-L218)):
* With a hostname: resolve it and compare the addresses, port ignored ([`net/backup.cpp:267-295`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L267-L295)).
* Otherwise: compare against `ips[0..nr-1]`, address only, port ignored. IPv4 is compared through
  the v4-mapped form; IPv6 needs the same scope id ([`net/backup.cpp:192-220`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L192-L220)).

Pass 2, if nothing matched:
* "detect" hosts ([`net/backup.cpp:495-507`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L495-L507)): the first host that is not deactivated and is
  `passive() && !hasname && detect` adopts the peer IP (`addiphasfamport`, which clears `detect`,
  [`net/passhost.hpp:219-232`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L219-L232)) and matches.
* Else, if any host had a label ([`net/backup.cpp:508-547`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L508-L547)): read **16 bytes** (one `recv`; a short
  read fails). For each host that is not deactivated and is
  `passive() && hasname && memcmp(name,label,16)==0`:
  * if `!noip`: accept only if `hasip(peer)`, or `detect` and the IP can be added; otherwise try
    the next host;
  * if `noip`: accept; if `detect` and the IP is unknown, add it.

Consequence: the connector sends its label **unconditionally** when it has one. The acceptor reads
it **only** when no unnamed host matched in pass 1 or by detect. Both sides must therefore agree:
either both use a label, or neither does.

### 1.5 How the direction is decided on one connection

There is no explicit negotiation. The direction comes from static configuration on both sides plus
the optional type byte:
* Acceptor host **without** `sendpassive`: the acceptor always **receives** on accepted
  connections. The connector must not send a type byte; otherwise that byte becomes the first
  nonce byte or command byte and the stream is corrupt.
* Acceptor host **with** `sendpassive` but without "receive" (`receivefrom&2 == 0`): every accepted
  connection from this host is used by the acceptor to **send** (`passivesender`). No type byte is
  read.
* Acceptor host with **both** `sendpassive` and "receive": the acceptor reads 1 type byte
  (unless `sendall`). `1` means the acceptor sends; anything else means the acceptor receives.
  The phone sends `1` on its active-receive connection (`sayactivereceive`, when the phone also
  sends to this host) and `2` on its sender connection (`saysender`, when the phone also
  active-receives from this host).

Configuration bits ([`datbackup.hpp:806-1066`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L806-L1066), [`net/passhost.hpp:62-103`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L62-L103)):
* `receivefrom = receive ? (reconnect?3:2) : ((sendto&&reconnect)?1:0)` with
  `reconnect = (receive && !passiveonly) || (sendto && !activeonly)` ([`datbackup.hpp:844`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L844), [`861`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L861), [`1021`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1021)).
* `receivedatafrom()` = `receivefrom & 2` ([`net/passhost.hpp:176-178`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L176-L178)).
* `sendpassive = sendto && passiveonly` ([`datbackup.hpp:864`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L864), [`884`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L884)).
* `activereceive` = non-zero index into the active-receive threads when `receive && activeonly`
  ([`datbackup.hpp:835`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L835), [`1047-1056`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1047-L1056)).
* `index >= 0` means "this host is a send target" ([`net/passhost.hpp:155-157`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L155-L157)).
* `noip = !testip` (cmdline `-i`) ([`datbackup.hpp:1004-1005`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1004-L1005), [`cmdline/main.cpp:419`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/cmdline/main.cpp#L419)).
* `detect` comes from cmdline `-a` ([`datbackup.hpp:984-991`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L984-L991)).
* `newconnection=true` only for a newly created host entry ([`datbackup.hpp:1026-1031`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1026-L1031)). It is
  cleared after the first successful magic exchange.

### 1.6 Version negotiation

There is none. The only "version" bytes are receivemagic bytes 11..13 (`00 00 00`), described as
version info and never checked ([`net/sender.cpp:167`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L167)). The connector also never checks byte 14
except for zero/non-zero. The only signalling is the "reset" flag (byte 13 of sendmagic, byte 14
of receivemagic = `00`).

---------------------------------------------------------------------------------------------

## 2. Message framing

### 2.1 Command codes ([`net/comtypes.hpp:26-48`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L26-L48))

Every command starts with a `uint16 com`:

| # | name | # | name | # | name |
|---|---|---|---|---|---|
| 1 | sopen | 9 | snewnums | 17 | sfileonce |
| 2 | smklen | 10 | snums | 18 | swakeupstream |
| 3 | swrite | 11 | snumnr | 19 | sglucose |
| 4 | sclose | 12 | **sack** | 20 | sresetdevices |
| 5 | sbackup | 13 | snuminit | 21 | sStartSendCalibrate |
| 7 | sbackupstop | 14 | sasklastnum | 22 | sBlueWatch |
| 8 | srender | 15 | saskfile | 99 | stest |
|   |   | 16 | suptodate |   |   |

Code 6 (srenum) is retired.

* `sendack` = `{uint16 com=12; uint16 nop=1}`, wire `0C 00 01 00` ([`net/comtypes.hpp:125-128`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L125-L128)).
* Ack answer: `uint32 ackres = 917521` = `0x000E0011`, wire `11 00 0E 00`
  ([`net/comtypes.hpp:129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L129)). The ack answer is **always plain, never encrypted, even in password
  mode**: `r_sendni(&ackres, 4)` ([`net/getcommand.cpp:161-171`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L161-L171)) and `getack()` reads 4 raw bytes
  ([`net/sendcommands.cpp:171-185`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L171-L185)).

The semantics and layouts of the individual commands belong to part 2. Framing only needs this:
the receiver walks a buffer of concatenated commands; after each command the cursor is rounded
up to a multiple of 4 (`it=aligner<4>(it)`, [`net/getcommand.cpp:377`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L377)). Each command's length
comes from its own header ([`net/getcommand.cpp:137-380`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L137-L380)). If any handler returns false, the
connection is dropped ([`net/getcommand.cpp:375-376`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L375-L376)).

Commands other than sBlueWatch, sbackupstop, swakeupstream, sbackup and sack are rejected
("Other side sends, but I am not configured to receive") unless `host->receivefrom & 2`
([`net/getcommand.cpp:152-157`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L152-L157)).

### 2.2 Plain mode (no password): sender side

* `sendcommand(buf,len)` ([`net/sendcommands.cpp:186-210`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L186-L210)) sends `buf`, then
  `pad = align4(len)-len` bytes (their **content is uninitialized stack memory**; the receiver
  skips them), then `0C 00 01 00` (sack), then blocks reading the 4-byte ack `11 00 0E 00`.
* `noacksendone` and `s_noacksendcommand` ([`net/sendcommands.cpp:269-274`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L269-L274), [`289-292`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L289-L292)) send the raw
  bytes only.

There is **no length prefix** in plain mode. The stream is a concatenation of self-delimiting,
4-aligned commands.

### 2.3 Plain mode: receiver side

`getcommandsnopass` ([`net/getcommand.cpp:393-463`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L393-L463)):
* `recv` into a 1 MiB buffer (it grows if one command is larger).
* `interpret()` returns `{it,comlen}` with `it>len` when the last command is incomplete. The
  partial tail is kept and more is read.
* `recv==0` or `-1` ends the connection.
* With fewer than 2 bytes left in the buffer, a `sack`-sized command is assumed to be incomplete
  ([`net/getcommand.cpp:142-146`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L142-L146)).

### 2.4 Encrypted mode: one frame

Sender (`gensendcommandpass`, [`net/sendcommands.cpp:216-258`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L216-L258)), for a command buffer `cmds` of
`buflen` bytes and a flag `askack`:

```
havelen = 4 + buflen
if askack:
    tussen  = align4(havelen) - havelen          # 0..3 zero bytes
    havelen = havelen + tussen + 4
    comlen  = buflen + tussen + 4
else:
    comlen  = buflen
takelen = max(16, havelen)
PLAINTEXT P (takelen bytes) =
    int32_le(comlen) || cmds || [zeros(tussen) || 0C 00 01 00 if askack] || zeros(takelen-havelen)
C, TAG = AsconStream.encrypt(P)                  # §3.3; len(C) == len(P); TAG 16 bytes
WIRE   = TAG(16) || C(takelen)                   # TAG FIRST, then ciphertext
then, if askack: read 4 PLAIN bytes and expect 11 00 0E 00
```

The padding bytes come from a static zero array (`zeros`, [`net/netstuff.hpp:31`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/netstuff.hpp#L31)). The minimum
frame on the wire is 32 bytes.

Receiver (`Connect::getcom`, [`net/getcommand.cpp:482-590`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L482-L590)):
1. read 16 bytes = TAG;
2. read 16 bytes = first ciphertext block and decrypt it. Plaintext bytes 0..3 = `datlen`
   (int32 LE). Reject if `datlen<0 || datlen>256 MiB` (`MAXDATA`, [`net/getcommand.cpp:480`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L480), [`527`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L527)).
3. `totlen = max(16, datlen+4)`; read `totlen-16` more bytes (loop) and decrypt them;
4. `decrypt_final` with TAG. If the tag is invalid, report "Encrypted with a different password"
   and drop the connection;
5. `interpret(P[4 .. 4+datlen])`. A trailing `sack` inside it triggers the plain 4-byte ack.

Every encrypted frame carries only **complete** commands. There is no reassembly across frames.
If `interpret` stops with a partial command (`last>0`), the code logs "Strange error" and drops
the connection ([`net/getcommand.cpp:467-479`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L467-L479)).

Callers:
* `sendcommand(pass,..)` is a frame with `askack=true` ([`net/sendcommands.cpp:263-268`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L263-L268)).
* `s_noacksendcommand(pass,..)` / `noacksendone` is a frame with `askack=false`
  ([`net/sendcommands.cpp:269-274`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L269-L274)).
* `r_noacksendcommand(pass,..)` is the same frame sent by the **data receiver** back to the
  sender ([`net/sendcommands.cpp:403-408`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L403-L408)).

### 2.5 Replies that travel from the data receiver back to the data sender

| Request | Plain-mode reply | Encrypted-mode reply |
|---|---|---|
| `sack` | 4 bytes `11 00 0E 00` | **same, plain** ([`net/getcommand.cpp:167`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L167)) |
| `sasklastnum` | 4 raw bytes int32 ([`net/getcommand.cpp:340-352`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L340-L352)) | normal frame (§2.4, no ack) with `cmds = int32 last`, i.e. P=`04 00 00 00 \|\| last \|\| 00*8`. The sender reads exactly 32 bytes in **one** recv ([`net/getcommand.cpp:627-667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L627-L667), [`nums/numdata.hpp:1272-1281`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/nums/numdata.hpp#L1272-L1281)) |
| `saskfile` | `dataonly{int32 len; data[len]}` padded to 4 ([`net/getcommand.cpp:852-876`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L852-L876)) | normal frame with that payload; the sender reads `16+max(16,4+payload)` bytes in one recv ([`SensorGlucoseData.hpp:1989-2008`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/SensorGlucoseData.hpp#L1989-L2008)) |
| `sopen` (legacy; the sender side is commented out, [`net/sendcommands.cpp:63-105`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L63-L105)) | 4 bytes `{int16 fp, int16 ~fp}` | **special short frame**: TAG(16) \|\| C(4), with **no int32 length header** (`sendcrypt`, [`net/getcommand.cpp:49-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L49-L62), [`64-87`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L64-L87)) |

All encrypted replies use the **same Ascon context** as the forward frames. See §3.4 for the
domain-separation flip.

### 2.6 Timeouts, keepalive, reconnection

There is no application-level keepalive or heartbeat. Liveness relies on TCP options and on the
sender's events.

* Acceptor socket after the handshake (receiver path), `receiversockopt`
  ([`net/backup.cpp:354-408`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L354-L408)):
  * `SO_KEEPALIVE=1`, `TCP_KEEPCNT=1`, `TCP_KEEPIDLE=50 s`, `TCP_KEEPINTVL=45 s`;
  * `SO_SNDTIMEO=60 s` and `TCP_USER_TIMEOUT=94000 ms` ([`net/sender.cpp:190-211`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L190-L211));
  * `SO_RCVTIMEO=0`, i.e. the receiver blocks forever and waits for data.
  The phone's active-receive socket gets the same settings ([`datbackup.cpp:548-549`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L548-L549)).
* Connector during the handshake: 180 s recv/send. On a sender connection afterwards: recv 60 s,
  send 300 s ([`TCPConnect.hpp:32-38`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp#L32-L38)). The ack wait is therefore bounded by 60 s.
* `connect()`: up to 60 s poll over all candidate IPs ([`TCPConnect.hpp:54-56`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/TCPConnect.hpp#L54-L56)).
* Sender reconnection ([`datbackup.hpp:1327-1365`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1327-L1365)): when an update fails, the sender closes and
  reopens the connection, then retries **once**. If the host is `sendpassive`, it does **not**
  reopen; it waits for the peer to connect again. Explicit reconnects come from the `wakereconnect`
  flag ([`datbackup.hpp:1303-1315`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1303-L1315)). Sending is event-driven: the sender thread waits on a condvar
  ([`datbackup.hpp:1406-1420`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1406-L1420)). Each update ends with `suptodate` (no ack), plus `srender` (with
  ack) when something was sent ([`datbackup.cpp:235-237`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L235-L237), [`datbackup.hpp:1391-1404`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L1391-L1404)).
* Active receiver (the phone in scenario B) ([`datbackup.cpp:442-566`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L442-L566)): a loop that waits on a
  condvar up to **70 s** (JUGGLUCO_APP, non-WearOS) or 30 s (others). It then connects, runs
  `activegetcommands` until the connection breaks, and loops again. A new TCP connection, and so a
  **new nonce and a fresh Ascon context**, is made every time.
* Acceptor: a new accepted connection for a host replaces the previous receiver socket of that
  host (shutdown + 1 s sleep + close, [`net/backup.cpp:561-568`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L561-L568), [`602-604`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L602-L604)).

---------------------------------------------------------------------------------------------

## 3. Encryption (password set)

### 3.1 Password to 16-byte key

`setpass` ([`datbackup.hpp:669-681`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L669-L681)), using the 16-byte constant `remix` at **[`datbackup.hpp:667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L667)**:

```
if len(password)==0: key = 16 x 00   -> "no password" (haspass() false)
L = min(len(password_bytes), 16)          # bytes as given (std::string_view; UTF-8 from Java/cmdline)
key[i] = password[i] XOR remix[i]  for i < L
key[i] = remix[i]                   for L <= i < 16
```

**Important:** this XOR is not just storage obfuscation. The stored `passhost_t::pass` array is
passed **directly** as the Ascon key: `ascon_aead128a_init(ctx, host->pass.data(), nonce)`
([`net/sender.cpp:85`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L85), [`net/getcommand.cpp:718`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L718)). A reimplementation must therefore apply the
`remix` constant from [`datbackup.hpp:667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L667) to get a compatible wire key.

* `passback` reverses the transform for display ([`datbackup.hpp:683-690`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L683-L690)).
* Password characters after the 16th are ignored.
* `haspass()` = key not all-zero ([`net/passhost.hpp:206-209`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L206-L209)). So a 16-byte password equal to
  `remix` would act as "no password" (edge case).
* Encryption is on when **this host entry** has a password ([`net/getcommand.cpp:737-744`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L737-L744),
  [`datbackup.cpp:398-401`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L398-L401), [`459-460`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L459-L460)). Both ends must configure the same password; nothing on the
  wire says whether encryption is on.

### 3.2 Nonce exchange (right after the magic / type byte)

* Connector `sendpassinit` ([`net/sender.cpp:68-88`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L68-L88)): `nonce[0..7]` = 8 random bytes, **sent**;
  then **receive** 8 bytes into `nonce[8..15]`.
* Acceptor `receivepassinit` ([`net/getcommand.cpp:692-725`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L692-L725)): receive 8 bytes into `nonce[0..7]`;
  `nonce[8..15]` = 8 random bytes, **sent**.
* Both run `ascon_aead128a_init(ctx, key16, nonce16)`.

The connector always provides the first half and the acceptor the second half, whatever the data
direction. In scenario B the phone (data receiver) sends nonceA, and the server's passive sender
replies with nonceB ([`datbackup.cpp:694`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L694)).

### 3.3 How Ascon-128a is used (modified LibAscon)

The library is LibAscon (Matjaž Guštin), API version 1.1.1 ([`LibAscon/inc/ascon.h:87-93`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/inc/ascon.h#L87-L93)), marked
"MODIFIED for Juggluco" ([`LibAscon/src/ascon_aead128a.c:1`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L1)). It implements **Ascon-128a v1.2**
(big-endian lanes):
* IV `0x80800C0800000000` ([`LibAscon/src/ascon_internal.h:46-51`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_internal.h#L46-L51));
* rate 16 bytes, p^12 / p^8;
* padding `0x80 << (56-8n)` ([`ascon_internal.h:69`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_internal.h#L69));
* round constants `F0..4B` ([`ascon_permutations.c:15-31`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_permutations.c#L15-L31)).
It is **not** the NIST SP 800-232 "Ascon-AEAD128" (little-endian) variant.

Modifications versus upstream:
1. `ascon_aead128a_encrypt_final`: `ascon_aead_cleanup(ctx)` is commented out and replaced by
   `ctx->bufstate.buffer_len = 0;` ([`LibAscon/src/ascon_aead128a.c:293-294`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L293-L294)).
2. `ascon_aead128a_decrypt_final`: the same change ([`LibAscon/src/ascon_aead128a.c:391-394`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L391-L394)).
3. Cosmetic: `#include "logs.hpp"` and a `LOGGER` call in `ascon_aead_cleanup`
   ([`LibAscon/src/ascon_aead_common.c:132-136`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead_common.c#L132-L136)). `crypt.h` defines an empty `resetctx`
   ([`net/crypt.h:25-28`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/crypt.h#L25-L28)).

Effect: after a frame's `*_final`, the sponge state (x0..x4), the key words and `flow_state` are
**kept**. The next frame continues from that state **without re-initialisation, without a new
nonce and without an extra permutation**. Upstream would zero the context. One context lives for
the whole TCP connection (`getcommandspassinit` / `activegetcommands` call
`ascon_aead_cleanup` only when the connection ends, [`net/getcommand.cpp:729-752`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L729-L752)). The sender
side keeps it in `crypts[ind]`, re-initialised by each new `sendpassinit` ([`datbackup.cpp:349-360`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L349-L360)).

Associated data: **never used** (no `assoc_data_update` calls anywhere in `net/`). Tag length is
always 16. On the wire the tag is placed **before** the ciphertext (§2.4).

#### Exact algorithm (to rebuild on top of a raw Ascon permutation)

State S = (x0..x4) as uint64; K = (k0,k1) = key as big-endian; `flow` is one of INIT, ENC or DEC.

```
INIT(K, N):                               # ascon_aead_common.c:12-30
  S = (IV, k0, k1, N[0..7]BE, N[8..15]BE); p12(S); x3^=k0; x4^=k1; buf=""; flow=INIT

DOMAIN_SEP_IF_NEEDED(op):                 # ascon_128a_finalise_assoc_data, ascon_aead128a.c:176-208
  if flow != op:  x4 ^= 1; buf=""         # (no AD is ever absorbed, so only the XOR happens)
  flow = op                               # (set by *_update; *_final does not change flow)

ENCRYPT_FRAME(P):                         # one encrypt_update chain + encrypt_final
  DOMAIN_SEP_IF_NEEDED(ENC)
  for each full 16-byte block B of P:     # buffered_accumulation, ascon_buffering.c:259-317
     x0^=B[0..7]BE; x1^=B[8..15]BE; C+=x0||x1 (BE); p8(S)
  last r bytes (0<=r<16), padded:         # ascon_aead128a.c:255-282
     r>=8: x0^=B0; x1^=B1(r-8 bytes)^PAD(r-8); C+=x0||x1[0..r-8)
     r<8 : x0^=B0(r bytes)^PAD(r);       C+=x0[0..r)
  x2^=k0; x3^=k1; p12(S); x3^=k0; x4^=k1; TAG = x3||x4 (BE)       # ascon_aead128a.c:285-291
  buf=""                                  # NO cleanup; S, K, flow persist

DECRYPT_FRAME(C, TAG):                    # mirror image, ascon_aead128a.c:298-396
  DOMAIN_SEP_IF_NEEDED(DEC)
  full blocks: P = S[x0,x1] ^ C; x0,x1 = C; p8(S)
  last r bytes: P = x ^ c (r bytes); replace the top r bytes of the lane with c; lane ^= PAD(r)
  x2^=k0; x3^=k1; p12(S); x3^=k0; x4^=k1; valid = (TAG == x3||x4)
  buf=""                                  # state persists EVEN IF the tag is invalid
                                          # (but the caller then drops the connection)
```

Source lines cited in the comments above: [`ascon_aead_common.c:12-30`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead_common.c#L12-L30), [`ascon_aead128a.c:176-208`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L176-L208),
[`ascon_buffering.c:259-317`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_buffering.c#L259-L317), [`ascon_aead128a.c:255-282`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L255-L282), [`ascon_aead128a.c:285-291`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L285-L291),
[`ascon_aead128a.c:298-396`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L298-L396).

Consequences:
* **Frame 1** of a connection is exactly standard Ascon-128a(K, N, AD="", P). A standard library
  can verify it (`DOMAIN_SEP` gives `x4^=1`, the same as the empty-AD rule of standard Ascon).
* **Frame n>1 in the same direction**: plaintext absorption starts directly on the state left after
  the previous tag. The previous tag is x3||x4, and x0, x1, x2 come from the final p12. There is no
  `x4^=1`, no permutation and no nonce. A one-shot standard API cannot do this. You need a
  sponge-level implementation, for example a copy of the reference C code with the cleanup removed,
  or the Python transliteration in [`jascon.py`](jascon.py).
* The `update` calls can be split at any boundary. The receiver decrypts 16 bytes and then the rest;
  the sender encrypts the 4-byte header, then `cmds`, then the padding. This does not change the
  result, because of the 16-byte buffering.

### 3.4 Both directions share one context: the direction flip

The data receiver uses the **same** ctx to encrypt its replies (§2.5, [`net/getcommand.cpp:49-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L49-L62), [`351`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L351), [`875`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L875)),
and the data sender uses it to decrypt them ([`net/sendcommands.cpp:48-62`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L48-L62),
[`net/getcommand.cpp:627-667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L627-L667)). Each `*_update`/`*_final` checks `flow`, so **every change of
direction** (DEC to ENC or ENC to DEC) applies `x4 ^= 1` once before processing the new frame
([`LibAscon/src/ascon_aead128a.c:224-229`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L224-L229), [`250-254`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L250-L254), [`312-317`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L312-L317), [`340-344`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_aead128a.c#L340-L344)). This includes the first frame
after init.

Both peers process the same transcript in the same order (sender ENC = receiver DEC), so both flip
at the same points. Rule for a reimplementation: keep a single sponge per connection; before
processing a frame, if its direction differs from the previous frame's direction (or it is the
first frame), XOR 1 into x4.

The plain 4-byte acks (`11 00 0E 00`) do **not** touch the cipher state.

### 3.5 Without a password

There is no nonce exchange and no tag. After the magic (and the type byte, if any) the raw
command stream of §2.2 and §2.3 starts immediately.

---------------------------------------------------------------------------------------------

## 4. Constants and test vectors

| What | Value | Where |
|---|---|---|
| sendmagic (14 B) | `DA AD BE ED DE ED BE EF D1 EF` + 4 random (last one != 0, or 00 = reset) | [`net/sendmagicinit.h:22`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendmagicinit.h#L22), [`net/sender.cpp:120-129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L120-L129), [`136-143`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L136-L143) |
| receivemagic (15 B) | `67 F9 2D 42 34 80 28 DE BA 81 3F 00 00 00 01` (last 00 = reset) | [`net/receivemagic.h:22`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/receivemagic.h#L22), [`net/backup.cpp:312-323`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L312-L323) |
| label | 16 B, zero-padded | [`net/passhost.hpp:59`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L59), [`185-199`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/passhost.hpp#L185-L199) |
| type byte | 1 = SENDPASSIVE (acceptor sends), 2 = RECEIVEFROM (acceptor receives) | [`datbackup.cpp:331-347`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.cpp#L331-L347), [`net/backup.cpp:571-593`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L571-L593) |
| nonce | 8 B connector + 8 B acceptor | [`net/sender.cpp:71-85`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sender.cpp#L71-L85), [`net/getcommand.cpp:694-718`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L694-L718) |
| key derivation constant `remix` | 16 B constant | [`datbackup.hpp:667`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L667) (used in `setpass` [`669-681`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L669-L681)) |
| sendack | `0C 00 01 00` | [`net/comtypes.hpp:125-128`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L125-L128) |
| ackres | `917521` = `11 00 0E 00` | [`net/comtypes.hpp:129`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/comtypes.hpp#L129) |
| tag length | 16, sent before the ciphertext | [`net/sendcommands.cpp:219`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L219), [`251`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L251), [`net/getcommand.cpp:490-492`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L490-L492) |
| min encrypted payload | 16 (plaintext padded with zeros) | [`net/sendcommands.cpp:231`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L231), [`248-250`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L248-L250) |
| MAXDATA | 256 MiB | [`net/getcommand.cpp:480`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L480) |
| Ascon-128a IV | `0x80800C0800000000` | [`LibAscon/src/ascon_internal.h:46-51`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/LibAscon/src/ascon_internal.h#L46-L51) |

The source contains **no test vectors**. The vectors below come from
[`jascon.py`](jascon.py) (in this folder), a line-by-line Python transliteration of the modified LibAscon. It
reproduces the official Ascon-128a KAT:
* Count 1 (key=nonce=`000102..0F`, AD="", PT="") gives tag `7A834E6F09210957067B10FD831F0078`;
* PT=`00` gives `6E652B55BFDC8CAD2EC43815B1666B1A3A`.

The chained frames below are derived, not captured from the app.

Key (already derived, i.e. after the `remix` XOR) = `000102030405060708090A0B0C0D0E0F`,
nonce = `0001020304050607` (connector) || `08090A0B0C0D0E0F` (acceptor). Frames A, B and D go
data-sender to data-receiver; C goes the other way.

```
A  suptodate, no ack     P=04000000100000000000000000000000
   wire=7ECA33ECCF7BE740151780FA00460BF6 6A480EFDC1B652606F3C06D33047C1B2   (tag || ct)
B  srender(0) + ack      P=08000000080000000C00010000000000
   wire=6ED83F7309D4F052C71EB59ECB8216A9 88375FF806F653F069BBD07EE6B1980F
C  reply int 1234 (dir flip)  P=04000000D20400000000000000000000
   wire=08733D8CCB89FD642B99D2ED43DA29C3 A3B65077DC8664D34E6E91F9D82A2470
D  suptodate (flip back) P=04000000100000000000000000000000
   wire=62EE2A17AC3DBFF7F64BC02A8AE2B0D1 969A9B5AE87DED3458FB5341BB94CCD5
```

Frame A equals one-shot standard Ascon-128a(K, N, "", P_A) (checked with an assert in the script).

---------------------------------------------------------------------------------------------

## 5. Uncertain / needs testing against the real app

1. **The app source may differ.** This analysis followed the Juggluco *server* (cmdline) build. The Android
   app shares this C++ (`net/`, `LibAscon`), but app builds can define `WEAROS_MESSAGES`,
   `JUGGLUCO_APP` and similar. The TCP byte layout seen here should be identical; confirm with a
   packet capture of a real phone.
2. **Default port.** The help text says 8795; `defaultport` falls back to "9996" unless
   `MIRRORPORT` is defined ([`datbackup.hpp:53-59`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/datbackup.hpp#L53-L59)).
3. **Python-derived vectors.** The chained-frame vectors (§4) come from a transliteration, not from
   the compiled C library (no C compiler available here). The first-frame KAT matches the official
   Ascon. Chaining and the direction flip follow the code by reading. Validate with one real
   encrypted session: capture the nonces and the first 2-3 frames, then decrypt offline.
4. **Type byte vs `sendall()`.** If the acceptor's host has `sendpassive`, `receive` **and**
   sends all three data kinds (`sendall`), it does not read the type byte
   ([`net/backup.cpp:579`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L579)). A phone that still sends one (because its host has both directions)
   would desynchronise. Check what the app UI allows, and test a two-way mirror.
5. **Label read is conditional.** The acceptor reads the 16-byte label only when no unnamed host
   (IP match, `noip` or `detect`) matched first. Mixing labelled and unlabelled host entries can
   desynchronise. To be safe, a reimplementation could peek: if the first 10 bytes are the
   sendmagic prefix, there is no label. **Suggested (not in the original):** the original never
   peeks.
6. **Single-recv assumptions.** The original requires that some reads return the full length in
   one `recv`: magic (14 B, [`net/backup.cpp:304`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L304)), label (16 B), first ciphertext block
   ([`net/getcommand.cpp:499`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L499)), encrypted replies ([`net/getcommand.cpp:641`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/getcommand.cpp#L641)). A reimplementation
   should read exactly N bytes in a loop. When it *sends* to a real phone, it must not coalesce
   the magic with later bytes (the phone answers the magic before sending more). The phone sends
   label and magic back to back, but waits after the magic.
7. **Reset flags.** Replying with receivemagic byte 14 = `00` makes the phone call `resethost()`
   and re-send everything. Verify that this is how a new server should get a full history, and
   that the phone does not loop if `00` is sent every time.
8. **Unencrypted padding garbage.** In plain mode the 0-3 alignment bytes before the trailing sack
   are uninitialized ([`net/sendcommands.cpp:193-194`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/sendcommands.cpp#L193-L194)). The receiver must skip them, not check them.
9. **Encrypted replies (sasklastnum/saskfile).** When the phone (data sender) asks the server for
   data, the server must reply with an encrypted frame on the shared context (direction flip). The
   phone reads it with a single `recv` of the exact size. This needs a test, especially for large
   `saskfile` payloads. `sopen` uses a different short frame (no length header) but appears unused
   by current senders.
10. **Tag failure.** After an invalid tag the state has already advanced. The original drops the
    connection, and the reimplementation must do the same; recovery inside a connection is not
    possible.
11. **Endianness and struct layout** are assumed LE with natural alignment. `fileonce_t` and the
    other structs are not packed. Verify against a capture (covered in part 2).
12. **The "myself" check** compares only sendmagic bytes 10..12. A reimplementation that is also
    a connector should randomise these per process, like the original.
13. **ICE / Bluetooth / Wear OS message carriers** (`net/ICE/*`, `WEAROS_MESSAGES`) use the same
    label → magic → nonce sequence through other transports ([`net/ICE/ICE.cpp:428`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/ICE/ICE.cpp#L428),
    [`net/backup.cpp:628-650`](https://github.com/j-kaltes/Juggluco/blob/43fdbe1ecf1e8e7543f66d89143ca505d3ada7ed/Common/src/main/cpp/net/backup.cpp#L628-L650)). They were not studied; out of scope for TCP 8795.
