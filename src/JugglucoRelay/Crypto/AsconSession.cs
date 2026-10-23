using System.Buffers.Binary;
using System.Security.Cryptography;

namespace JugglucoRelay.Crypto;

/// <summary>
/// Ascon-128a v1.2 (big-endian lanes, not the NIST SP 800-232 variant) in the modified form
/// used by Juggluco: after each frame the state is NOT cleared, and the next frame resumes
/// where the previous one stopped, without a new nonce. One state per TCP connection,
/// shared by both directions: each change of direction applies x4 ^= 1.
/// Reference: Juggluco LibAscon/src/ascon_aead128a.c (lines 293 and 391: cleanup removed)
/// and docs/protocol/spec-1-connection.md §3.3–3.4.
/// </summary>
public sealed class AsconSession
{
    public const int TagLength = 16;
    private const int Rate = 16;
    private const ulong Iv128a = 0x80800C0800000000UL;

    private static readonly ulong[] RoundConstants =
        [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5, 0x96, 0x87, 0x78, 0x69, 0x5A, 0x4B];

    private enum Flow { Initialised, Encrypt, Decrypt }

    private ulong x0, x1, x2, x3, x4;
    private readonly ulong k0, k1;
    private Flow flow = Flow.Initialised;

    public AsconSession(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != 16) throw new ArgumentException("The key must be 16 bytes", nameof(key));
        if (nonce.Length != 16) throw new ArgumentException("The nonce must be 16 bytes", nameof(nonce));
        k0 = BinaryPrimitives.ReadUInt64BigEndian(key);
        k1 = BinaryPrimitives.ReadUInt64BigEndian(key[8..]);
        x0 = Iv128a;
        x1 = k0;
        x2 = k1;
        x3 = BinaryPrimitives.ReadUInt64BigEndian(nonce);
        x4 = BinaryPrimitives.ReadUInt64BigEndian(nonce[8..]);
        Permute(12);
        x3 ^= k0;
        x4 ^= k1;
    }

    private AsconSession(AsconSession other)
    {
        (x0, x1, x2, x3, x4) = (other.x0, other.x1, other.x2, other.x3, other.x4);
        (k0, k1, flow) = (other.k0, other.k1, other.flow);
    }

    /// <summary>
    /// Decrypts the first block of a frame without advancing the state. Used to read the
    /// length at the head of the frame before knowing how many bytes to wait for: the first block
    /// decrypted here is identical to the one <see cref="Decrypt"/> will produce on the whole frame.
    /// </summary>
    public void PeekFirstBlock(ReadOnlySpan<byte> firstBlock, Span<byte> plaintext)
    {
        if (firstBlock.Length != Rate) throw new ArgumentException("A 16-byte block is required", nameof(firstBlock));
        var copy = new AsconSession(this);
        copy.SwitchTo(Flow.Decrypt);
        BinaryPrimitives.WriteUInt64BigEndian(plaintext, copy.x0 ^ BinaryPrimitives.ReadUInt64BigEndian(firstBlock));
        BinaryPrimitives.WriteUInt64BigEndian(plaintext[8..], copy.x1 ^ BinaryPrimitives.ReadUInt64BigEndian(firstBlock[8..]));
    }

    /// <summary>Encrypts a whole frame. <paramref name="ciphertext"/> has the same length as the plaintext.</summary>
    public void Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag)
    {
        if (ciphertext.Length < plaintext.Length) throw new ArgumentException("Output buffer too short", nameof(ciphertext));
        if (tag.Length < TagLength) throw new ArgumentException("Tag buffer too short", nameof(tag));
        SwitchTo(Flow.Encrypt);

        int full = plaintext.Length / Rate * Rate;
        for (int i = 0; i < full; i += Rate)
        {
            x0 ^= BinaryPrimitives.ReadUInt64BigEndian(plaintext[i..]);
            x1 ^= BinaryPrimitives.ReadUInt64BigEndian(plaintext[(i + 8)..]);
            BinaryPrimitives.WriteUInt64BigEndian(ciphertext[i..], x0);
            BinaryPrimitives.WriteUInt64BigEndian(ciphertext[(i + 8)..], x1);
            Permute(8);
        }

        var rest = plaintext[full..];
        var restOut = ciphertext[full..];
        int r = rest.Length;
        if (r >= 8)
        {
            x0 ^= BinaryPrimitives.ReadUInt64BigEndian(rest);
            x1 ^= ReadVarLen(rest[8..]) ^ Padding(r - 8);
            BinaryPrimitives.WriteUInt64BigEndian(restOut, x0);
            WriteVarLen(restOut[8..], x1, r - 8);
        }
        else
        {
            x0 ^= ReadVarLen(rest) ^ Padding(r);
            WriteVarLen(restOut, x0, r);
        }

        Finalise();
        BinaryPrimitives.WriteUInt64BigEndian(tag, x3);
        BinaryPrimitives.WriteUInt64BigEndian(tag[8..], x4);
    }

    /// <summary>
    /// Decrypts a whole frame and returns whether the tag is valid. As in Juggluco, the state
    /// advances even with an invalid tag: in that case the connection must be closed.
    /// </summary>
    public bool Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        if (plaintext.Length < ciphertext.Length) throw new ArgumentException("Output buffer too short", nameof(plaintext));
        if (tag.Length != TagLength) throw new ArgumentException("The tag must be 16 bytes", nameof(tag));
        SwitchTo(Flow.Decrypt);

        int full = ciphertext.Length / Rate * Rate;
        for (int i = 0; i < full; i += Rate)
        {
            ulong c0 = BinaryPrimitives.ReadUInt64BigEndian(ciphertext[i..]);
            ulong c1 = BinaryPrimitives.ReadUInt64BigEndian(ciphertext[(i + 8)..]);
            BinaryPrimitives.WriteUInt64BigEndian(plaintext[i..], x0 ^ c0);
            BinaryPrimitives.WriteUInt64BigEndian(plaintext[(i + 8)..], x1 ^ c1);
            x0 = c0;
            x1 = c1;
            Permute(8);
        }

        var rest = ciphertext[full..];
        var restOut = plaintext[full..];
        int r = rest.Length;
        if (r >= 8)
        {
            ulong c0 = BinaryPrimitives.ReadUInt64BigEndian(rest);
            int h = r - 8;
            ulong c1 = ReadVarLen(rest[8..]);
            BinaryPrimitives.WriteUInt64BigEndian(restOut, x0 ^ c0);
            WriteVarLen(restOut[8..], x1 ^ c1, h);
            x0 = c0;
            x1 = (x1 & ~ByteMask(h)) | c1;
            x1 ^= Padding(h);
        }
        else
        {
            ulong c0 = ReadVarLen(rest);
            WriteVarLen(restOut, x0 ^ c0, r);
            x0 = (x0 & ~ByteMask(r)) | c0;
            x0 ^= Padding(r);
        }

        Finalise();
        Span<byte> expected = stackalloc byte[TagLength];
        BinaryPrimitives.WriteUInt64BigEndian(expected, x3);
        BinaryPrimitives.WriteUInt64BigEndian(expected[8..], x4);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    // ascon_128a_finalise_assoc_data: Juggluco never uses associated data, so only the domain
    // separation remains, applied every time the direction changes (including the first frame).
    private void SwitchTo(Flow next)
    {
        if (flow != next)
        {
            x4 ^= 1UL;
            flow = next;
        }
    }

    private void Finalise()
    {
        x2 ^= k0;
        x3 ^= k1;
        Permute(12);
        x3 ^= k0;
        x4 ^= k1;
    }

    private void Permute(int rounds)
    {
        for (int i = 12 - rounds; i < 12; i++)
        {
            x2 ^= RoundConstants[i];
            x0 ^= x4; x4 ^= x3; x2 ^= x1;
            ulong t0 = ~x0 & x1, t1 = ~x1 & x2, t2 = ~x2 & x3, t3 = ~x3 & x4, t4 = ~x4 & x0;
            x0 ^= t1; x1 ^= t2; x2 ^= t3; x3 ^= t4; x4 ^= t0;
            x1 ^= x0; x0 ^= x4; x3 ^= x2; x2 = ~x2;
            x0 ^= ulong.RotateRight(x0, 19) ^ ulong.RotateRight(x0, 28);
            x1 ^= ulong.RotateRight(x1, 61) ^ ulong.RotateRight(x1, 39);
            x2 ^= ulong.RotateRight(x2, 1) ^ ulong.RotateRight(x2, 6);
            x3 ^= ulong.RotateRight(x3, 10) ^ ulong.RotateRight(x3, 17);
            x4 ^= ulong.RotateRight(x4, 7) ^ ulong.RotateRight(x4, 41);
        }
    }

    private static ulong Padding(int bytes) => 0x80UL << (56 - 8 * bytes);

    private static ulong ByteMask(int bytes) => bytes == 0 ? 0 : ulong.MaxValue << (64 - 8 * bytes);

    private static ulong ReadVarLen(ReadOnlySpan<byte> src)
    {
        ulong v = 0;
        for (int i = 0; i < src.Length; i++) v |= (ulong)src[i] << (56 - 8 * i);
        return v;
    }

    private static void WriteVarLen(Span<byte> dst, ulong v, int bytes)
    {
        for (int i = 0; i < bytes; i++) dst[i] = (byte)(v >> (56 - 8 * i));
    }
}
