using System.Text;

namespace JugglucoRelay.Crypto;

/// <summary>
/// Ascon key derived from a connection's password, like <c>setpass</c> in
/// Juggluco datbackup.hpp:667-681: XOR with a fixed constant, padded with the constant
/// itself up to 16 bytes; characters beyond the sixteenth are ignored.
/// </summary>
public static class JugglucoKey
{
    private static ReadOnlySpan<byte> Remix =>
        [0x19, 0xED, 0xA0, 0x4A, 0x94, 0x9D, 0x0C, 0xD7, 0x82, 0x4A, 0x74, 0xA9, 0x0E, 0x71, 0x84, 0x8B];

    /// <summary>Returns null if the password is empty: unencrypted connection.</summary>
    public static byte[]? FromPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var bytes = Encoding.UTF8.GetBytes(password);
        var key = new byte[16];
        int len = Math.Min(bytes.Length, 16);
        for (int i = 0; i < 16; i++)
            key[i] = i < len ? (byte)(bytes[i] ^ Remix[i]) : Remix[i];
        // haspass() in Juggluco treats an all-zero key as "no password"
        return key.All(b => b == 0) ? null : key;
    }
}
