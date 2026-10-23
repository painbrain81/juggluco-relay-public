using JugglucoRelay.Crypto;

namespace JugglucoRelay.Tests;

// Vectors generated with docs/protocol/jascon.py, a line-by-line transcription of the modified
// LibAscon in Juggluco LibAscon. The first two match the official Ascon-128a v1.2 KATs.
public class AsconSessionTests
{
    private static readonly byte[] Seq = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private static (byte[] ct, byte[] tag) Encrypt(AsconSession s, byte[] pt)
    {
        var ct = new byte[pt.Length];
        var tag = new byte[AsconSession.TagLength];
        s.Encrypt(pt, ct, tag);
        return (ct, tag);
    }

    [Fact]
    public void KatCount1_EmptyPlaintext()
    {
        var (ct, tag) = Encrypt(new AsconSession(Seq, Seq), []);
        Assert.Empty(ct);
        Assert.Equal("7A834E6F09210957067B10FD831F0078", Convert.ToHexString(tag));
    }

    [Fact]
    public void KatCount2_OneBytePlaintext()
    {
        var (ct, tag) = Encrypt(new AsconSession(Seq, Seq), [0x00]);
        Assert.Equal("6E652B55BFDC8CAD2EC43815B1666B1A3A", Convert.ToHexString([.. ct, .. tag]));
    }

    [Fact]
    public void JugglucoFrames_ChainedAcrossDirectionChanges()
    {
        // Frames A..D of jascon.py: suptodate, srender with ack, reply to sasklastnum, suptodate
        string[] pts =
        [
            "04000000100000000000000000000000",
            "08000000080000000C00010000000000",
            "04000000D20400000000000000000000",
            "04000000100000000000000000000000",
        ];
        string[] wires =
        [
            "7ECA33ECCF7BE740151780FA00460BF66A480EFDC1B652606F3C06D33047C1B2",
            "6ED83F7309D4F052C71EB59ECB8216A988375FF806F653F069BBD07EE6B1980F",
            "08733D8CCB89FD642B99D2ED43DA29C3A3B65077DC8664D34E6E91F9D82A2470",
            "62EE2A17AC3DBFF7F64BC02A8AE2B0D1969A9B5AE87DED3458FB5341BB94CCD5",
        ];
        bool[] fromSender = [true, true, false, true];
        var sender = new AsconSession(Seq, Seq);
        var receiver = new AsconSession(Seq, Seq);
        for (int i = 0; i < pts.Length; i++)
        {
            var (enc, dec) = fromSender[i] ? (sender, receiver) : (receiver, sender);
            var pt = Convert.FromHexString(pts[i]);
            var (ct, tag) = Encrypt(enc, pt);
            Assert.Equal(wires[i], Convert.ToHexString([.. tag, .. ct]));
            var back = new byte[ct.Length];
            Assert.True(dec.Decrypt(ct, tag, back));
            Assert.Equal(pt, back);
        }
    }

    [Fact]
    public void PasswordKey_AndPartialBlocks()
    {
        var key = JugglucoKey.FromPassword("Passw0rd!")!;
        Assert.Equal("498CD339E3AD7EB3A34A74A90E71848B", Convert.ToHexString(key));

        var nonce = Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90");
        var a = new AsconSession(key, nonce);
        var b = new AsconSession(key, nonce);
        (char dir, int len, string tag, string ct)[] seq =
        [
            ('a', 37, "867D49F7EE66236D296E6EFC2E743D48", "99780828AE0D67D85380B9290CB1E7E43FFB6B26917E63E3B1E32EED3DC479D72B76FABADB"),
            ('a', 24, "56F52B8D9EF04736CF0F809230A19603", "D320F79878496E4BDC298DFEAE04F331211E343DDA367210"),
            ('b', 31, "27CF9108B2DD43CDAB1232905CF81726", "F327EA3DBD6C0F7EC63FCCDB4F868ED323308B99AD6A13797AF046944B0784"),
            ('b', 32, "4EAD1BA016897CC899E91070379A7AD9", "36EA0F920FE8D9F2DA90C8452ADB2017A2C0EC5AB9A19C752CD7422559C1CD64"),
            ('a', 16, "A7DC8EA7435E1C7068F080DE66BB8AB5", "584EDFE25271C3554534D8833F9D8EFE"),
            ('b', 8, "288753EFFC1D8A9D4DF2F6727764C487", "4D84C29B0A619125"),
            ('a', 100, "5D71767D4BFA4F637D416371EF3DFD94", "6DEE9417218C2A4CD676F8DC1E0E584EBADDE9E37F844E19A033870358924B0AF52E425246FE1A12976649DD622090F450735932F79FD1C50F139F5E3023E40178B4FD7DEAB9CFB0758120BCF0C92C7566E0B4A37C185973B58199D0A495ADB7C2049D78"),
        ];
        for (int i = 0; i < seq.Length; i++)
        {
            var (dir, len, expTag, expCt) = seq[i];
            var pt = Enumerable.Range(0, len).Select(j => (byte)((i * 31 + j * 7) & 0xFF)).ToArray();
            var (enc, dec) = dir == 'a' ? (a, b) : (b, a);
            var (ct, tag) = Encrypt(enc, pt);
            Assert.Equal(expTag, Convert.ToHexString(tag));
            Assert.Equal(expCt, Convert.ToHexString(ct));
            var back = new byte[len];
            Assert.True(dec.Decrypt(ct, tag, back));
            Assert.Equal(pt, back);
        }
    }

    [Fact]
    public void Decrypt_RejectsTamperedTag()
    {
        var a = new AsconSession(Seq, Seq);
        var b = new AsconSession(Seq, Seq);
        var (ct, tag) = Encrypt(a, new byte[20]);
        tag[0] ^= 1;
        Assert.False(b.Decrypt(ct, tag, new byte[20]));
    }

    [Fact]
    public void EmptyPassword_MeansNoEncryption()
    {
        Assert.Null(JugglucoKey.FromPassword(""));
        Assert.Null(JugglucoKey.FromPassword(null));
    }
}
