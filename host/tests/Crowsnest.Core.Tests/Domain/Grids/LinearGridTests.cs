using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Tests.Domain.Grids;

public class LinearGridTests
{
    // The nav1.standby registry entry (§5.2): 108.00-117.95 MHz in 50 kHz steps.
    private static readonly LinearGrid Nav = new(108_000, 117_950, 50, parent: 1000);
    private static readonly CursorLevel Mhz = new("mhz", 1000, CursorWrap.Clamp, 0..3);
    private static readonly CursorLevel Khz = new("khz", 50, CursorWrap.WrapWithinParent, 4..6);

    // The ap.altitude entry: 0-50,000 ft in 100 ft steps, no parent.
    private static readonly LinearGrid Altitude = new(0, 50_000, 100);
    private static readonly CursorLevel Thousands = new("coarse", 1000, CursorWrap.Clamp, 0..2);
    private static readonly CursorLevel Hundreds = new("fine", 100, CursorWrap.Clamp, 3..4);

    [Fact]
    public void NavHasTwentyChannelsPerMhzAcrossTenMhz()
    {
        Assert.Equal(200, Nav.Count);
        Assert.Equal((108_000, 117_950), (Nav.Min, Nav.Max));
    }

    [Theory]
    [InlineData(108_000, true)]
    [InlineData(110_350, true)]
    [InlineData(117_950, true)]
    [InlineData(110_325, false)] // off the 50 kHz grid
    [InlineData(107_950, false)] // below the band
    [InlineData(118_000, false)] // above it
    public void ContainsOnlyFiftyKhzValuesInTheBand(int khz, bool expected) =>
        Assert.Equal(expected, Nav.Contains(khz));

    [Fact]
    public void MaxIsRoundedDownOntoTheGrid()
    {
        LinearGrid grid = new(0, 1_050, 100);

        Assert.Equal(1_000, grid.Max);
        Assert.False(grid.Contains(1_050));
    }

    [Theory]
    [InlineData(110_324, 110_300)]
    [InlineData(110_326, 110_350)]
    [InlineData(110_325, 110_300)] // exact tie snaps down
    [InlineData(100_000, 108_000)] // below the band
    [InlineData(120_000, 117_950)] // above it
    public void SnapsToTheNearestValue(int value, int expected) => Assert.Equal(expected, Nav.Snap(value));

    [Theory]
    [InlineData(110_300, 1, 110_350)]
    [InlineData(110_300, -2, 110_200)]
    [InlineData(108_950, 1, 108_000)] // wraps within the MHz, the way a real radio does
    [InlineData(108_000, -1, 108_950)]
    [InlineData(117_950, 1, 117_000)] // top of the band wraps within 117, not off the end
    [InlineData(110_300, 20, 110_300)] // a full turn of the MHz comes back round
    [InlineData(110_300, -21, 110_250)]
    public void KhzCursorStepsFiftyKhzAndWrapsWithinTheMhz(int from, int detents, int expected) =>
        Assert.Equal(expected, Nav.Step(from, detents, Khz));

    [Fact]
    public void CarryingKhzCursorRollsIntoTheNextMhzAndStopsAtTheBandEdges()
    {
        CursorLevel carry = Khz with { Wrap = CursorWrap.Carry };

        Assert.Equal(109_000, Nav.Step(108_950, 1, carry));
        Assert.Equal(108_950, Nav.Step(109_000, -1, carry));
        Assert.Equal(117_950, Nav.Step(117_900, 5, carry));
        Assert.Equal(108_000, Nav.Step(108_050, -5, carry));
    }

    [Theory]
    [InlineData(110_300, 1, 111_300)]
    [InlineData(110_300, -2, 108_300)]
    [InlineData(117_300, 3, 117_300)] // clamps at the top
    [InlineData(108_300, -2, 108_300)] // and the bottom
    public void MhzCursorKeepsTheFractionAndClamps(int from, int detents, int expected) =>
        Assert.Equal(expected, Nav.Step(from, detents, Mhz));

    [Fact]
    public void WrappingMhzCursorGoesRoundTheBandKeepingTheFraction()
    {
        CursorLevel wrap = Mhz with { Wrap = CursorWrap.WrapWithinParent };

        Assert.Equal(108_050, Nav.Step(117_050, 1, wrap));
        Assert.Equal(117_050, Nav.Step(108_050, -1, wrap));
    }

    [Fact]
    public void MhzMoveThatLandsPastAShortBandEdgeSnapsToTheLastValue()
    {
        // A grid whose top MHz is cut short: 117.500 is the last value.
        LinearGrid shortTop = new(108_000, 117_500, 50, parent: 1000);

        Assert.Equal(117_500, shortTop.Step(116_900, 1, Mhz));
    }

    [Fact]
    public void KhzWrapInAMhzCutShortByTheRangeStaysInsideTheRange()
    {
        // The bottom MHz starts at .500: wrapping down from there lands on .950, not .000.
        LinearGrid shortBottom = new(108_500, 117_950, 50, parent: 1000);

        Assert.Equal(108_950, shortBottom.Step(108_500, -1, Khz));
        Assert.Equal(108_500, shortBottom.Step(108_950, 1, Khz));
    }

    [Theory]
    [InlineData(4_900, 1, 5_000)] // carries into the thousands
    [InlineData(4_500, 1, 4_600)]
    [InlineData(49_900, 3, 50_000)] // clamps at the top
    [InlineData(200, -5, 0)] // and the bottom
    public void AltitudeFineCursorStepsAHundredFeet(int from, int detents, int expected) =>
        Assert.Equal(expected, Altitude.Step(from, detents, Hundreds));

    [Theory]
    [InlineData(4_500, 1, 5_500)]
    [InlineData(49_500, 1, 50_000)] // with no parent a coarse step is plain arithmetic, clamped
    [InlineData(500, -1, 0)]
    public void AltitudeCoarseCursorStepsAThousandFeet(int from, int detents, int expected) =>
        Assert.Equal(expected, Altitude.Step(from, detents, Thousands));

    [Fact]
    public void WrappingCursorWithNoParentGoesRoundTheWholeRange()
    {
        CursorLevel wrap = Hundreds with { Wrap = CursorWrap.WrapWithinParent };

        Assert.Equal(0, Altitude.Step(50_000, 1, wrap));
        Assert.Equal(50_000, Altitude.Step(0, -1, wrap));
    }

    [Fact]
    public void NegativeRangesWork()
    {
        // Not the vertical-speed grid (that is SignedLinearGrid), but the arithmetic must hold below zero.
        LinearGrid grid = new(-1_000, 1_000, 100, parent: 1000);
        CursorLevel fine = new("fine", 100, CursorWrap.WrapWithinParent, 0..1);

        Assert.True(grid.Contains(-300));
        Assert.Equal(-300, grid.Snap(-320));
        Assert.Equal(-1_000, grid.Step(-100, 1, fine)); // -100 sits in the block [-1000, -1)
        Assert.Equal(-100, grid.Step(-1_000, -1, fine));
    }

    [Fact]
    public void OffGridStartIsSnappedBeforeStepping()
    {
        // 110.320 is not a NAV channel; it snaps to 110.300 and one click up is 110.350.
        Assert.Equal(110_350, Nav.Step(110_320, 1, Khz));
    }

    [Fact]
    public void EveryValueStepsUpAndBackToItself()
    {
        foreach (int khz in Values(Nav))
        {
            Assert.Equal(khz, Nav.Step(Nav.Step(khz, 1, Khz), -1, Khz));
            Assert.Equal(khz, Nav.Step(khz, 0, Khz));
        }
    }

    [Fact]
    public void EveryStepLandsOnTheGrid()
    {
        foreach (int detents in (int[])[-50, -7, -1, 1, 3, 17, 200])
        {
            Assert.All(Values(Nav), khz => Assert.True(Nav.Contains(Nav.Step(khz, detents, Khz))));
            Assert.All(Values(Nav), khz => Assert.True(Nav.Contains(Nav.Step(khz, detents, Mhz))));
        }
    }

    [Fact]
    public void ACursorOffTheGridStepIsRejected()
    {
        CursorLevel odd = Khz with { Step = 25 };

        Assert.Throws<ArgumentException>(() => Nav.Step(110_300, 1, odd));
    }

    [Fact]
    public void BadConstructionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearGrid(0, 100, 0));
        Assert.Throws<ArgumentException>(() => new LinearGrid(100, 0, 10));
        Assert.Throws<ArgumentException>(() => new LinearGrid(0, 1_000, 50, parent: 75)); // not a multiple
        Assert.Throws<ArgumentException>(() => new LinearGrid(0, 1_000, 50, parent: 50)); // not larger
    }

    private static IEnumerable<int> Values(LinearGrid grid) =>
        Enumerable.Range(0, grid.Count).Select(i => grid.Min + i * grid.StepSize);
}
