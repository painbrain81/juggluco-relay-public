"""Line-by-line Python transliteration of Juggluco's modified LibAscon (Ascon-128a,
streaming, NO cleanup after *_final, state chained across messages).
Used only to produce reference vectors for the spec (spec-1-connection.md, section 4).
Source references (file:line) point to Juggluco at commit 43fdbe1 (11.0.x), directory
Common/src/main/cpp; LibAscon files are under LibAscon/src/, the others under net/."""
M = (1 << 64) - 1
IV128A = 0x80800C0800000000  # ascon_internal.h:46-51
RC = [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5, 0x96, 0x87, 0x78, 0x69, 0x5A, 0x4B]

def rotr(x, n):
    return ((x >> n) | (x << (64 - n))) & M

def rnd(s, c):
    x0, x1, x2, x3, x4 = s
    x2 ^= c
    x0 ^= x4; x4 ^= x3; x2 ^= x1
    t0 = (~x0 & M) & x1; t1 = (~x1 & M) & x2; t2 = (~x2 & M) & x3
    t3 = (~x3 & M) & x4; t4 = (~x4 & M) & x0
    x0 ^= t1; x1 ^= t2; x2 ^= t3; x3 ^= t4; x4 ^= t0
    x1 ^= x0; x0 ^= x4; x3 ^= x2; x2 = ~x2 & M
    x0 ^= rotr(x0, 19) ^ rotr(x0, 28)
    x1 ^= rotr(x1, 61) ^ rotr(x1, 39)
    x2 ^= rotr(x2, 1) ^ rotr(x2, 6)
    x3 ^= rotr(x3, 10) ^ rotr(x3, 17)
    x4 ^= rotr(x4, 7) ^ rotr(x4, 41)
    s[:] = [x0, x1, x2, x3, x4]

def p12(s):
    for c in RC: rnd(s, c)

def p8(s):
    for c in RC[4:]: rnd(s, c)

def be(b):
    return int.from_bytes(b.ljust(8, b'\0')[:8], 'big') if b else 0

def tobe(x, n=8):
    return x.to_bytes(8, 'big')[:n]

def pad(n):
    return (0x80 << (56 - 8 * n)) & M

class Ctx:
    INIT, ENC, DEC = 1, 2, 3

    def __init__(self, key, nonce):
        self.k0 = be(key[:8]); self.k1 = be(key[8:16])
        self.s = [IV128A, self.k0, self.k1, be(nonce[:8]), be(nonce[8:16])]
        p12(self.s)
        self.s[3] ^= self.k0; self.s[4] ^= self.k1
        self.buf = b''
        self.flow = Ctx.INIT

    def _fin_ad(self):  # ascon_128a_finalise_assoc_data, never any AD in Juggluco
        self.s[4] ^= 1
        self.buf = b''

    def _tagstate(self):
        s = self.s
        s[2] ^= self.k0; s[3] ^= self.k1
        p12(s)
        s[3] ^= self.k0; s[4] ^= self.k1
        return tobe(s[3]) + tobe(s[4])

    def enc_update(self, pt):
        if self.flow != Ctx.ENC: self._fin_ad()
        self.flow = Ctx.ENC
        data = self.buf + pt; out = b''
        while len(data) >= 16:
            self.s[0] ^= be(data[:8]); self.s[1] ^= be(data[8:16])
            out += tobe(self.s[0]) + tobe(self.s[1]); p8(self.s); data = data[16:]
        self.buf = data
        return out

    def enc_final(self):
        if self.flow != Ctx.ENC: self._fin_ad()
        b = self.buf; n = len(b); out = b''
        if n >= 8:
            self.s[0] ^= be(b[:8]); self.s[1] ^= be(b[8:]); self.s[1] ^= pad(n - 8)
            out = tobe(self.s[0]) + tobe(self.s[1], n - 8)
        else:
            self.s[0] ^= be(b); self.s[0] ^= pad(n); out = tobe(self.s[0], n)
        tag = self._tagstate()
        self.buf = b''   # NO cleanup (Juggluco modification)
        return out, tag

    def dec_update(self, ct):
        if self.flow != Ctx.DEC: self._fin_ad()
        self.flow = Ctx.DEC
        data = self.buf + ct; out = b''
        while len(data) >= 16:
            c0 = be(data[:8]); c1 = be(data[8:16])
            out += tobe(self.s[0] ^ c0) + tobe(self.s[1] ^ c1)
            self.s[0] = c0; self.s[1] = c1; p8(self.s); data = data[16:]
        self.buf = data
        return out

    def dec_final(self, tag):
        if self.flow != Ctx.DEC: self._fin_ad()
        b = self.buf; n = len(b)
        def mask(k):
            x = 0
            for i in range(k): x |= 0xFF << (56 - 8 * i)
            return x
        if n >= 8:
            c0 = be(b[:8]); c1 = be(b[8:]); h = n - 8
            out = tobe(self.s[0] ^ c0) + tobe(self.s[1] ^ c1, h)
            self.s[0] = c0; self.s[1] = (self.s[1] & ~mask(h) & M) | c1; self.s[1] ^= pad(h)
        else:
            c0 = be(b)
            out = tobe(self.s[0] ^ c0, n)
            self.s[0] = (self.s[0] & ~mask(n) & M) | c0; self.s[0] ^= pad(n)
        t = self._tagstate()
        self.buf = b''
        return out, t == tag

# ---- Juggluco framing helpers -------------------------------------------
import struct

def send_frame(ctx, cmds, askack):
    """gensendcommandpass (sendcommands.cpp:216-258). Returns wire bytes tag||ct."""
    buflen = len(cmds)
    havelen = 4 + buflen
    tussen = 0
    if askack:
        tussen = ((havelen + 3) & ~3) - havelen
        havelen += tussen + 4
        comlen = buflen + tussen + 4
    else:
        comlen = buflen
    takelen = max(16, havelen)
    pt = struct.pack('<i', comlen) + cmds
    if askack:
        pt += b'\0' * tussen + struct.pack('<HH', 12, 1)
    pt += b'\0' * (takelen - havelen)
    ct = ctx.enc_update(pt)
    rest, tag = ctx.enc_final()
    return tag + ct + rest, pt

def recv_frame(ctx, wire):
    tag = wire[:16]; body = wire[16:]
    pt = ctx.dec_update(body)
    rest, ok = ctx.dec_final(tag)
    return pt + rest, ok

if __name__ == '__main__':
    # 1) Official Ascon-128a KAT, Count=1 (key=nonce=00..0F, AD="", PT="")
    c = Ctx(bytes(range(16)), bytes(range(16)))
    ct, tag = c.enc_final()
    print('KAT count1 tag', tag.hex().upper())
    # Count=2 style check: PT=00 (1 byte)
    c = Ctx(bytes(range(16)), bytes(range(16)))
    ct = c.enc_update(b'\x00'); r, tag = c.enc_final()
    print('KAT PT=00 ct||tag', (ct + r + tag).hex().upper())

    # 2) Juggluco chained-session vector
    key = bytes(range(16))            # already the 16-byte derived key (see spec §3.1)
    nonce = bytes.fromhex('0001020304050607') + bytes.fromhex('08090A0B0C0D0E0F')
    snd = Ctx(key, nonce); rcv = Ctx(key, nonce)
    # msg A: sender -> receiver: one 'suptodate' command (u16 16, u16 0), no ack request
    wireA, ptA = send_frame(snd, struct.pack('<HH', 16, 0), False)
    print('A plaintext', ptA.hex().upper()); print('A wire     ', wireA.hex().upper())
    out, ok = recv_frame(rcv, wireA); assert ok and out == ptA
    # msg B: sender -> receiver: srender(type=0) with ack request
    wireB, ptB = send_frame(snd, struct.pack('<HH', 8, 0), True)
    print('B plaintext', ptB.hex().upper()); print('B wire     ', wireB.hex().upper())
    out, ok = recv_frame(rcv, wireB); assert ok and out == ptB
    # msg C: receiver -> sender (direction switch): reply to sasklastnum, int last=1234
    wireC, ptC = send_frame(rcv, struct.pack('<i', 1234), False)
    print('C plaintext', ptC.hex().upper()); print('C wire     ', wireC.hex().upper())
    out, ok = recv_frame(snd, wireC); assert ok and out == ptC
    # msg D: sender -> receiver again (switch back)
    wireD, ptD = send_frame(snd, struct.pack('<HH', 16, 0), False)
    print('D plaintext', ptD.hex().upper()); print('D wire     ', wireD.hex().upper())
    out, ok = recv_frame(rcv, wireD); assert ok and out == ptD
    # Sanity: msg A must equal plain one-shot Ascon-128a(key,nonce,AD="",ptA)
    fresh = Ctx(key, nonce); ct = fresh.enc_update(ptA); r, t = fresh.enc_final()
    assert t + ct + r == wireA
    print('all asserts ok')
