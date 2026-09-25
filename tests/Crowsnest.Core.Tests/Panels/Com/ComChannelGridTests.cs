using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests.Panels.Com;

public class ComChannelGridTests
{
    private static readonly ComChannelGrid EightThree = new(ChannelSpacing.EightPointThreeThree);
    private static readonly ComChannelGrid TwentyFive = new(ChannelSpacing.TwentyFiveKhz);

    // The cursors from the spec's com1.standby registry entry (§5.2).
    private static readonly CursorLevel Mhz = new("mhz", 1000, CursorWrap.Clamp, 0..3);
    private static readonly CursorLevel Khz = new("khz", 1, CursorWrap.WrapWithinParent, 4..7);

    [Fact]
    public void EightThreeFractionsMatchTheSpecListInEveryHundredKhzBlock()
    {
        // Spec §5.3, verbatim. This is the rule the rest of the file leans on.
        int[] legal = [0, 5, 10, 15, 25, 30, 35, 40, 50, 55, 60, 65, 75, 80, 85, 90];

        for (int block = ComChannelGrid.BandMinKhz; block < ComChannelGrid.BandMaxKhz; block += 100)
        {
            int[] fractions = [.. Enumerable.Range(block, 100).Where(EightThree.Contains).Select(k => k - block)];
            Assert.Equal(legal, fractions);
        }
    }

    [Theory]
    [InlineData(118_020)]
    [InlineData(118_045)]
    [InlineData(118_070)]
    [InlineData(118_095)]
    public void TheFourGapsAreNotChannels(int khz) => Assert.False(EightThree.Contains(khz));

    [Fact]
    public void TwentyFiveKhzIsTheQuarterFractionsOnly()
    {
        int[] fractions = [.. Enumerable.Range(121_000, 100).Where(TwentyFive.Contains).Select(k => k - 121_000)];
        Assert.Equal([0, 25, 50, 75], fractions);
    }

    [Fact]
    public void EveryTwentyFiveKhzChannelIsAlsoAnEightThreeChannel()
    {
        // The property that makes guessing 8.33 when the aircraft is on 25 kHz fail safe
        // in one direction: nothing reachable under 25 kHz disappears.
        Assert.All(TwentyFive.Channels, khz => Assert.True(EightThree.Contains(khz)));
    }

    [Fact]
    public void ChannelCountsCoverTheWholeBand()
    {
        // 19 MHz (118-136 inclusive) x 40 or x 160 names per MHz.
        Assert.Equal(760, TwentyFive.Channels.Count);
        Assert.Equal(3040, EightThree.Channels.Count);
        Assert.Equal((118_000, 136_975), (TwentyFive.Min, TwentyFive.Max));
        Assert.Equal((118_000, 136_990), (EightThree.Min, EightThree.Max));
    }

    [Theory]
    [InlineData(118_015, 1, 118_025)] // steps over the .020 gap
    [InlineData(118_025, -1, 118_015)]
    [InlineData(118_040, 1, 118_050)] // steps over .045
    [InlineData(118_000, 4, 118_025)] // one 25 kHz slot is four names
    [InlineData(121_500, 16, 121_600)] // 100 kHz is sixteen names
    public void EightThreeKhzCursorStepsByChannelNotByKhz(int from, int detents, int expected) =>
        Assert.Equal(expected, EightThree.Step(from, detents, Khz));

    [Theory]
    [InlineData(118_990, 1, 118_000)]
    [InlineData(118_000, -1, 118_990)]
    [InlineData(136_990, 1, 136_000)] // top of the band wraps within 136, not off the end
    [InlineData(121_500, 160, 121_500)] // a full turn of the MHz comes back round
    [InlineData(121_500, -161, 121_490)]
    public void KhzCursorWrapsWithinTheMhz(int from, int detents, int expected) =>
        Assert.Equal(expected, EightThree.Step(from, detents, Khz));

    [Theory]
    [InlineData(118_975, 1, 118_000)]
    [InlineData(118_000, -1, 118_975)]
    [InlineData(124_850, 1, 124_875)] // what the C172 did in spike 0(a)
    public void TwentyFiveKhzCursorWrapsWithinTheMhz(int from, int detents, int expected) =>
        Assert.Equal(expected, TwentyFive.Step(from, detents, Khz));

    [Fact]
    public void CarryingKhzCursorRollsIntoTheNextMhzAndStopsAtTheBandEdges()
    {
        CursorLevel carry = Khz with { Wrap = CursorWrap.Carry };

        Assert.Equal(119_000, EightThree.Step(118_990, 1, carry));
        Assert.Equal(118_990, EightThree.Step(119_000, -1, carry));
        Assert.Equal(136_990, EightThree.Step(136_985, 5, carry));
        Assert.Equal(118_000, EightThree.Step(118_005, -5, carry));
    }

    [Theory]
    [InlineData(121_500, 1, 122_500)]
    [InlineData(121_500, -3, 118_500)]
    [InlineData(136_500, 3, 136_500)] // clamps at the top
    [InlineData(118_500, -2, 118_500)] // and the bottom
    [InlineData(118_015, 1, 119_015)] // an 8.33-only fraction survives the move
    public void MhzCursorKeepsTheFractionAndClamps(int from, int detents, int expected) =>
        Assert.Equal(expected, EightThree.Step(from, detents, Mhz));

    [Fact]
    public void WrappingMhzCursorGoesRoundTheBand()
    {
        CursorLevel wrap = Mhz with { Wrap = CursorWrap.WrapWithinParent };

        Assert.Equal(118_500, EightThree.Step(136_500, 1, wrap));
        Assert.Equal(136_500, EightThree.Step(118_500, -1, wrap));
    }

    [Fact]
    public void MhzMoveThatLandsPastAShortBandEdgeSnapsToTheLastChannel()
    {
        // A grid whose top MHz is cut short: 136.500 is the last channel.
        ComChannelGrid shortTop = new(ChannelSpacing.TwentyFiveKhz, maxKhz: 136_500);

        Assert.Equal(136_500, shortTop.Step(135_900, 1, Mhz));
    }

    [Theory]
    [InlineData(118_017, 118_015)]
    [InlineData(118_021, 118_025)]
    [InlineData(118_020, 118_015)] // exact tie snaps down
    [InlineData(100_000, 118_000)] // below the band
    [InlineData(150_000, 136_990)] // above it
    public void EightThreeSnapsToTheNearestName(int value, int expected) =>
        Assert.Equal(expected, EightThree.Snap(value));

    [Theory]
    [InlineData(118_012, 118_000)]
    [InlineData(118_013, 118_025)]
    [InlineData(118_005, 118_000)] // an 8.33 name is not a 25 kHz channel
    public void TwentyFiveKhzSnapsToTheNearestQuarter(int value, int expected) =>
        Assert.Equal(expected, TwentyFive.Snap(value));

    [Fact]
    public void OffGridStartIsSnappedBeforeStepping()
    {
        // 118.020 does not exist; it snaps to 118.015 and one click up is 118.025.
        Assert.Equal(118_025, EightThree.Step(118_020, 1, Khz));
    }

    [Fact]
    public void ZeroDetentsIsANoOp()
    {
        Assert.All(EightThree.Channels, khz => Assert.Equal(khz, EightThree.Step(khz, 0, Khz)));
    }

    [Fact]
    public void EveryChannelStepsUpAndBackToItself()
    {
        Assert.All(EightThree.Channels, khz => Assert.Equal(khz, EightThree.Step(EightThree.Step(khz, 1, Khz), -1, Khz)));
        Assert.All(TwentyFive.Channels, khz => Assert.Equal(khz, TwentyFive.Step(TwentyFive.Step(khz, 1, Khz), -1, Khz)));
    }

    [Fact]
    public void EveryStepLandsOnAChannel()
    {
        foreach (int detents in (int[])[-50, -7, -1, 1, 3, 17, 200])
        {
            Assert.All(EightThree.Channels, khz => Assert.True(EightThree.Contains(EightThree.Step(khz, detents, Khz))));
        }
    }

    [Fact]
    public void AnEmptyRangeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ComChannelGrid(ChannelSpacing.TwentyFiveKhz, 118_001, 118_024));
    }
}
