using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Tests.Domain.Grids;

public class SignedLinearGridTests
{
    // Autopilot vertical speed (§5.3 roadmap): ±8000 fpm in 100 fpm steps.
    private static readonly SignedLinearGrid VerticalSpeed = new(-8_000, 8_000, 100);
    private static readonly CursorLevel Hundreds = new("fine", 100, CursorWrap.Clamp, 0..5);
    private static readonly CursorLevel Thousands = new("coarse", 1_000, CursorWrap.Carry, 0..5);

    [Theory]
    [InlineData(0, true)]
    [InlineData(-8_000, true)]
    [InlineData(8_000, true)]
    [InlineData(-700, true)]
    [InlineData(750, false)]
    [InlineData(8_100, false)]
    public void ContainsMultiplesOfTheStepInRange(int fpm, bool expected) =>
        Assert.Equal(expected, VerticalSpeed.Contains(fpm));

    [Theory]
    [InlineData(-100, 1, 0)] // crosses into level flight
    [InlineData(0, 1, 100)]
    [InlineData(0, -1, -100)]
    [InlineData(-200, 5, 300)]
    [InlineData(7_900, 3, 8_000)] // stops at the top
    [InlineData(-7_900, -3, -8_000)] // and the bottom
    public void FineCursorStepsAHundredFeetAndStopsAtTheEnds(int from, int detents, int expected) =>
        Assert.Equal(expected, VerticalSpeed.Step(from, detents, Hundreds));

    [Theory]
    [InlineData(-500, 1, 500)]
    [InlineData(7_500, 1, 8_000)]
    [InlineData(-7_500, -1, -8_000)]
    public void CoarseCursorStepsAThousandFeetAndStopsAtTheEnds(int from, int detents, int expected) =>
        Assert.Equal(expected, VerticalSpeed.Step(from, detents, Thousands));

    [Fact]
    public void AWrappingCursorIsRejectedBecauseItWouldJumpFromClimbToDescent()
    {
        CursorLevel wrap = Hundreds with { Wrap = CursorWrap.WrapWithinParent };

        Assert.Throws<ArgumentException>(() => VerticalSpeed.Step(8_000, 1, wrap));
    }

    [Theory]
    [InlineData(149, 100)]
    [InlineData(151, 200)]
    [InlineData(150, 100)] // an exact tie snaps toward zero
    [InlineData(-150, -100)] // on both sides
    [InlineData(50, 0)]
    [InlineData(-50, 0)]
    [InlineData(12_000, 8_000)]
    [InlineData(-12_000, -8_000)]
    [InlineData(int.MinValue, -8_000)]
    [InlineData(int.MaxValue, 8_000)]
    public void SnapsToTheNearestValueWithTiesTowardZero(int value, int expected) =>
        Assert.Equal(expected, VerticalSpeed.Snap(value));

    [Fact]
    public void SnapIsSymmetricAboutZero()
    {
        Assert.All(Enumerable.Range(-9_000, 18_001), fpm => Assert.Equal(-VerticalSpeed.Snap(fpm), VerticalSpeed.Snap(-fpm)));
    }

    [Fact]
    public void EveryValueStepsUpAndBackToItselfAwayFromTheEnds()
    {
        for (int fpm = -7_900; fpm <= 7_900; fpm += 100)
        {
            Assert.Equal(fpm, VerticalSpeed.Step(VerticalSpeed.Step(fpm, 1, Hundreds), -1, Hundreds));
            Assert.Equal(fpm, VerticalSpeed.Step(fpm, 0, Hundreds));
        }
    }

    [Fact]
    public void EveryStepLandsOnTheGrid()
    {
        foreach (int detents in (int[])[-200, -7, -1, 1, 13, 200])
        {
            for (int fpm = -8_000; fpm <= 8_000; fpm += 100)
            {
                Assert.True(VerticalSpeed.Contains(VerticalSpeed.Step(fpm, detents, Hundreds)));
                Assert.True(VerticalSpeed.Contains(VerticalSpeed.Step(fpm, detents, Thousands)));
            }
        }
    }

    [Fact]
    public void BadConstructionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignedLinearGrid(-100, 100, 0));
        Assert.Throws<ArgumentException>(() => new SignedLinearGrid(0, 8_000, 100)); // no negative side
        Assert.Throws<ArgumentException>(() => new SignedLinearGrid(-8_000, -100, 100)); // no positive side
        Assert.Throws<ArgumentException>(() => new SignedLinearGrid(-8_050, 8_000, 100)); // zero would be off the grid
        Assert.Throws<ArgumentException>(() => VerticalSpeed.Step(0, 1, Hundreds with { Step = 50 }));
    }
}
