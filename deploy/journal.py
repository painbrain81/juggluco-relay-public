#!/usr/bin/env python3
"""Lists the entries of journal.bin from a position onwards (read only).

    sudo python3 journal.py <position> [count]

Entry format (src/JugglucoRelay/Storage/Journal.cs): int32 length, int32 flags
(bit 0 = asks for an ack), uint32 CRC32, command, zero padding to a multiple of 4.
"""
import struct
import sys
import zlib

PATH = "/var/lib/juggluco-relay/journal.bin"
NAMES = {8: "srender", 10: "snums", 11: "snumnr", 13: "snuminit", 17: "sfileonce",
         19: "sglucose", 20: "sresetdevices", 21: "sStartSendCalibrate"}

start = int(sys.argv[1])
count = int(sys.argv[2]) if len(sys.argv) > 2 else 40

with open(PATH, "rb") as f:
    f.seek(start)
    for _ in range(count):
        at = f.tell()
        head = f.read(12)
        if len(head) < 12:
            break
        length, flags, crc = struct.unpack("<iiI", head)
        cmd = f.read(length)
        f.read((4 - length % 4) % 4)
        code = struct.unpack_from("<H", cmd)[0]
        info = ""
        if code == 17:
            namelen, totlen, nr, dowith = struct.unpack_from("<HiHH", cmd, 2)
            name = cmd[12 + 8 * nr:12 + 8 * nr + namelen].split(b"\0")[0].decode(errors="replace")
            segs = [struct.unpack_from("<ii", cmd, 12 + 8 * i) for i in range(nr)]
            tail = length - (12 + 8 * nr + namelen + sum(l for _, l in segs))
            info = f"{name} dowith=0x{dowith:04X} segments={segs} extra={cmd[length - tail:].hex() if tail > 0 else '-'}"
        else:
            info = cmd[:32].hex()
        ok = "ok" if zlib.crc32(cmd) == crc else "BAD CRC"
        print(f"{at:>9} {NAMES.get(code, code)!s:>20} len={length:<5} ack={flags & 1} {ok} {info}")
