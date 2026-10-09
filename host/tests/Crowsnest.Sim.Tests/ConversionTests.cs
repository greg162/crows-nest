using Crowsnest.Core.Domain;

namespace Crowsnest.Sim.Tests;

public class PayloadEncoderTests
{
    [Theory]
    [InlineData(122_800, 122_800_000u)] // spec §7.3: COM_STBY_RADIO_SET_HZ 122800000
    [InlineData(118_005, 118_005_000u)] // an 8.33 channel name, sent as-is (spike 0(a))
    [InlineData(136_990, 136_990_000u)]
    public void HzIsKhzTimesAThousand(int khz, uint expected) => Assert.Equal(expected, PayloadEncoder.Encode(khz, PayloadEncoding.Hz));

    [Fact]
    public void RawIsTheValueAsItIs() => Assert.Equal(12_000u, PayloadEncoder.Encode(12_000, PayloadEncoding.Raw));

    [Theory]
    [InlineData(1_500, 1_500u)]
    [InlineData(-1_500, 0xFFFF_FA24u)] // two's complement, read back as -1500
    public void SignedKeepsTheSign(int fpm, uint expected) => Assert.Equal(expected, PayloadEncoder.Encode(fpm, PayloadEncoding.Signed));

    [Theory]
    [InlineData(7700, 0x7700u)]
    [InlineData(1200, 0x1200u)]
    [InlineData(77, 0x0077u)]
    [InlineData(0, 0u)]
    public void Bcd16PacksTheDigitsIntoNibbles(int code, uint expected) => Assert.Equal(expected, PayloadEncoder.Encode(code, PayloadEncoding.Bcd16));

    [Theory]
    [InlineData(-1, PayloadEncoding.Hz)]
    [InlineData(-1, PayloadEncoding.Raw)]
    [InlineData(10_000, PayloadEncoding.Bcd16)]
    [InlineData(-1, PayloadEncoding.Bcd16)]
    [InlineData(int.MaxValue, PayloadEncoding.Hz)] // would overflow a uint in Hz
    public void ValuesTheEncodingCannotHoldAreRejected(int value, PayloadEncoding encoding)
    {
        Exception e = Record.Exception(() => PayloadEncoder.Encode(value, encoding));

        Assert.True(e is ArgumentOutOfRangeException or OverflowException, $"{e?.GetType().Name ?? "nothing"} thrown");
    }
}

public class ValueConverterTests
{
    private static readonly ReadBinding Hz = new(ReadSource.SimVar, "COM ACTIVE FREQUENCY:1", "Hz", 0.001);
    private static readonly ReadBinding Mhz = new(ReadSource.SimVar, "COM ACTIVE FREQUENCY:1", "MHz", 1000);
    private static readonly ReadBinding Enum = new(ReadSource.SimVar, "COM SPACING MODE:1", "Enum", 1);
    private static readonly ReadBinding Bco16 = new(ReadSource.SimVar, "TRANSPONDER CODE:1", "BCO16", 1);

    [Theory]
    [InlineData(0x7700, 7700)]
    [InlineData(0x1200, 1200)]
    [InlineData(0x0077, 77)]
    [InlineData(0, 0)] // squawk 0000 is a code, unlike a frequency of 0
    public void APackedCodeIsUnpackedToTheDigitsItReads(int packed, int expected) =>
        Assert.Equal(expected, ValueConverter.ToCanonical(packed, Bco16));

    [Theory]
    [InlineData(7700.0)] // 0x1E14: the code already unpacked, which a nibble of E gives away
    [InlineData(0x10000)]
    [InlineData(-1.0)]
    [InlineData(4608.5)]
    [InlineData(double.NaN)]
    public void WhatIsNotPackedDigitsIsNoValue(double raw) =>
        Assert.Null(ValueConverter.ToCanonical(raw, Bco16));

    [Theory]
    [InlineData(1200)]
    [InlineData(7777)]
    [InlineData(0)]
    public void UnpackingUndoesTheWriteEncoding(int code) =>
        Assert.Equal(code, ValueConverter.ToCanonical(PayloadEncoder.Encode(code, PayloadEncoding.Bcd16), Bco16));

    [Theory]
    [InlineData(121_500_000.0, 121_500)]
    [InlineData(118_005_000.0, 118_005)]
    [InlineData(124_849_999.9999, 124_850)] // float noise rounds, not truncates
    public void HzBecomesKhz(double hz, int expected) => Assert.Equal(expected, ValueConverter.ToCanonical(hz, Hz));

    [Fact]
    public void MhzRoundsRatherThanTruncating()
    {
        // 121.5 × 1000 is 121499.99999999999 in a double; a cast would give 121.499.
        Assert.Equal(121_500, ValueConverter.ToCanonical(121.5, Mhz));
        Assert.Equal(118_005, ValueConverter.ToCanonical(118.005, Mhz));
    }

    [Fact]
    public void ZeroHzIsNoValue()
    {
        // Spec §5.3: COM 1 read 0.000 / 0.000 mid-load in the TriStar.
        Assert.Null(ValueConverter.ToCanonical(0, Hz));
        Assert.Null(ValueConverter.ToCanonical(0, Mhz));
    }

    [Fact]
    public void ZeroIsARealValueForNonFrequencies() => Assert.Equal(0, ValueConverter.ToCanonical(0, Enum));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e30)]
    public void ValuesThatAreNotAnIntAreNoValue(double raw) => Assert.Null(ValueConverter.ToCanonical(raw, Enum));
}
