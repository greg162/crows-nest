using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Tests.Domain.Grids;

public class WrappingGridTests
{
    // The ap.heading registry entry (§5.2): 0-359 in 1° steps, tens and ones cursors.
    private static readonly WrappingGrid Heading = new(0, 359, 1);
    private static readonly CursorLevel Tens = new("tens", 10, CursorWrap.Carry, 0..2);
    private static readonly CursorLevel Ones = new("ones", 1, CursorWrap.WrapWithinParent, 2..3);

    [Fact]
    public void HeadingIsThreeHundredAndSixtyValuesRoundACircle()
    {
        Assert.Equal(360, Heading.Count);
        Assert.Equal(360, Heading.Period);
        Assert.True(Heading.Contains(0));
        Assert.True(Heading.Contains(359));
        Assert.False(Heading.Contains(360));
        Assert.False(Heading.Contains(-1));
    }

    [Theory]
    [InlineData(359, 1, 0)] // the spec's 359 → 0
    [InlineData(0, -1, 359)] // and back
    [InlineData(180, 1, 181)]
    [InlineData(10, -25, 345)]
    [InlineData(90, 360, 90)] // a full turn comes back round
    [InlineData(90, -721, 89)]
    public void OnesCursorGoesRoundTheCircle(int from, int detents, int expected) =>
        Assert.Equal(expected, Heading.Step(from, detents, Ones));

    [Theory]
    [InlineData(355, 1, 5)] // carries round through north
    [InlineData(5, -1, 355)]
    [InlineData(270, 3, 300)]
    [InlineData(123, 36, 123)]
    public void TensCursorGoesRoundTheCircleKeepingTheOnes(int from, int detents, int expected) =>
        Assert.Equal(expected, Heading.Step(from, detents, Tens));

    [Fact]
    public void CarryAndWrapWithinParentAreTheSameOnACircle()
    {
        CursorLevel carry = Ones with { Wrap = CursorWrap.Carry };

        Assert.All(Enumerable.Range(0, 360), deg => Assert.Equal(Heading.Step(deg, 7, Ones), Heading.Step(deg, 7, carry)));
    }

    [Fact]
    public void AClampingCursorIsRejected()
    {
        CursorLevel clamp = Ones with { Wrap = CursorWrap.Clamp };

        Assert.Throws<ArgumentException>(() => Heading.Step(90, 1, clamp));
    }

    [Theory]
    [InlineData(360, 0)] // some aircraft report north as 360
    [InlineData(-10, 350)]
    [InlineData(725, 5)]
    [InlineData(90, 90)]
    public void OutOfRangeValuesAreBroughtOntoTheCircle(int value, int expected) =>
        Assert.Equal(expected, Heading.Snap(value));

    [Theory]
    [InlineData(357, 355)]
    [InlineData(358, 0)] // nearer to 360 than to 355, so it rounds up through north
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    public void CoarseCircleSnapsAcrossNorth(int value, int expected) =>
        Assert.Equal(expected, new WrappingGrid(0, 355, 5).Snap(value));

    [Fact]
    public void AnExactTieSnapsDown() => Assert.Equal(0, new WrappingGrid(0, 350, 10).Snap(5));

    [Fact]
    public void AMinimumOtherThanZeroWorks()
    {
        // A 1-360 compass card, for aircraft that never show 000.
        WrappingGrid card = new(1, 360, 1);

        Assert.Equal(1, card.Step(360, 1, Ones));
        Assert.Equal(360, card.Step(1, -1, Ones));
        Assert.Equal(360, card.Snap(0));
    }

    [Fact]
    public void EveryValueStepsUpAndBackToItself()
    {
        foreach (CursorLevel cursor in (CursorLevel[])[Tens, Ones])
        {
            Assert.All(Enumerable.Range(0, 360), deg => Assert.Equal(deg, Heading.Step(Heading.Step(deg, 1, cursor), -1, cursor)));
            Assert.All(Enumerable.Range(0, 360), deg => Assert.Equal(deg, Heading.Step(deg, 0, cursor)));
        }
    }

    [Fact]
    public void EveryStepLandsOnTheGrid()
    {
        foreach (int detents in (int[])[-400, -37, -1, 1, 9, 361])
        {
            Assert.All(Enumerable.Range(0, 360), deg => Assert.True(Heading.Contains(Heading.Step(deg, detents, Tens))));
            Assert.All(Enumerable.Range(0, 360), deg => Assert.True(Heading.Contains(Heading.Step(deg, detents, Ones))));
        }
    }

    [Fact]
    public void BadConstructionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WrappingGrid(0, 359, 0));
        Assert.Throws<ArgumentException>(() => new WrappingGrid(359, 0, 1));
        Assert.Throws<ArgumentException>(() => new WrappingGrid(0, 358, 5)); // not whole steps
        Assert.Throws<ArgumentException>(() => Heading.Step(0, 1, Ones with { Step = 0 }));
    }
}
