using JugglucoRelay.Export;

namespace JugglucoRelay.Tests;

// Expected values as printed by glibc printf("%+g") / ("%g")
public class StreamFormatTests
{
    [Theory]
    [InlineData(0.45f, true, "+0.45")]
    [InlineData(-1.2f, true, "-1.2")]
    [InlineData(0f, true, "+0")]
    [InlineData(0.00001f, true, "+1e-05")]
    [InlineData(1234567f, true, "+1.23457e+06")]
    [InlineData(2f, false, "2")]
    [InlineData(5.5f, false, "5.5")]
    public void FormatG_MatchesLibc(float value, bool sign, string expected) =>
        Assert.Equal(expected, StreamExporter.FormatG(value, sign));

    [Fact]
    public void FormatG_NaN_KeepsSignBit()
    {
        // .NET's float.NaN has the sign bit set; the one written by the app does not ("+nan" in stream.tsv)
        Assert.Equal("+nan", StreamExporter.FormatG(BitConverter.Int32BitsToSingle(0x7FC00000), sign: true));
        Assert.Equal("-nan", StreamExporter.FormatG(BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00000)), sign: true));
    }
}
