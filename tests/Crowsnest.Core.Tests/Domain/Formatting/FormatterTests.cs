using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Tests.Domain.Formatting;

public class FormatterTests
{
    private static readonly FrequencyFormatter Freq3 = new(decimals: 3);
    private static readonly FrequencyFormatter Freq2 = new(decimals: 2);
    private static readonly GroupedFormatter Thousands = new();
    private static readonly GroupedFormatter SignedThousands = new(signed: true);
    private static readonly PaddedFormatter Deg3 = new(digits: 3);
    private static readonly PaddedFormatter Code4 = new(digits: 4);

    [Theory]
    [InlineData(121_500, "121.500")]
    [InlineData(118_005, "118.005")] // an 8.33 channel name keeps its third digit
    [InlineData(118_505, "118.505")] // the sim's own off-grid value, shown as it is
    [InlineData(136_990, "136.990")]
    public void Freq3ShowsEveryKhzDigit(int khz, string expected) => Assert.Equal(expected, Freq3.Format(khz));

    [Theory]
    [InlineData(108_000, "108.00")]
    [InlineData(110_350, "110.35")]
    [InlineData(117_950, "117.95")]
    [InlineData(110_359, "110.35")] // the last digit is dropped, not rounded
    public void Freq2ShowsTensOfKhz(int khz, string expected) => Assert.Equal(expected, Freq2.Format(khz));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(500, "500")]
    [InlineData(9_000, "9,000")]
    [InlineData(12_000, "12,000")]
    [InlineData(-200, "-200")]
    public void ThousandsGroupsDigits(int feet, string expected) => Assert.Equal(expected, Thousands.Format(feet));

    [Theory]
    [InlineData(0, "0")] // level flight has no sign
    [InlineData(700, "+700")]
    [InlineData(1_500, "+1,500")]
    [InlineData(-1_500, "-1,500")]
    public void SignedThousandsShowsClimbAndDescent(int fpm, string expected) => Assert.Equal(expected, SignedThousands.Format(fpm));

    [Theory]
    [InlineData(0, "000")]
    [InlineData(5, "005")]
    [InlineData(90, "090")]
    [InlineData(359, "359")]
    public void Deg3PadsToThreeDigits(int degrees, string expected) => Assert.Equal(expected, Deg3.Format(degrees));

    [Theory]
    [InlineData(7700, "7700")]
    [InlineData(77, "0077")]
    [InlineData(0, "0000")]
    public void Code4PadsASquawkToFourDigits(int code, string expected) => Assert.Equal(expected, Code4.Format(code));

    [Fact]
    public void PlaceholdersHaveTheShapeOfAValue()
    {
        Assert.Equal("---.---", Freq3.Placeholder);
        Assert.Equal("---.--", Freq2.Placeholder);
        Assert.Equal("---", Deg3.Placeholder);
        Assert.Equal("----", Code4.Placeholder);
        Assert.Equal("---", Thousands.Placeholder);
    }

    [Fact]
    public void NoFormatterThrowsOnAnyInt()
    {
        IValueFormatter[] all = [Freq3, Freq2, Thousands, SignedThousands, Deg3, Code4];
        int[] awkward = [int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue];

        Assert.All(all, formatter => Assert.All(awkward, value => Assert.False(string.IsNullOrEmpty(formatter.Format(value)))));
    }

    [Fact]
    public void BadConstructionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrequencyFormatter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrequencyFormatter(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaddedFormatter(0));
    }
}
