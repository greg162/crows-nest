using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Transponder;

namespace Crowsnest.Core.Tests.Panels.Transponder;

public class DigitGridTests
{
    private static readonly DigitGrid Squawk = new();

    // One cursor per digit, turning that digit alone, as a transponder's knobs do.
    private static readonly CursorLevel First = new("d1", 1000, CursorWrap.WrapWithinParent, 0..1);
    private static readonly CursorLevel Second = new("d2", 100, CursorWrap.WrapWithinParent, 1..2);
    private static readonly CursorLevel Third = new("d3", 10, CursorWrap.WrapWithinParent, 2..3);
    private static readonly CursorLevel Fourth = new("d4", 1, CursorWrap.WrapWithinParent, 3..4);

    [Fact]
    public void ThereAre4096CodesFrom0000To7777()
    {
        Assert.Equal(4096, Squawk.Codes.Count);
        Assert.Equal((0, 7777), (Squawk.Min, Squawk.Max));
        Assert.Equal(Squawk.Codes.Order(), Squawk.Codes);
    }

    [Theory]
    [InlineData(7700, true)] // emergency
    [InlineData(7000, true)] // European VFR
    [InlineData(1200, true)] // US VFR
    [InlineData(0, true)]
    [InlineData(7777, true)]
    [InlineData(7800, false)] // 8 is not an octal digit
    [InlineData(1239, false)]
    [InlineData(10000, false)]
    [InlineData(-1, false)]
    public void ContainsOnlyFourOctalDigits(int code, bool expected) => Assert.Equal(expected, Squawk.Contains(code));

    [Theory]
    [InlineData(7677, 1, 7777)]
    [InlineData(7777, 1, 7077)] // the digit wraps 7 → 0 without touching its neighbours
    [InlineData(7077, -1, 7777)]
    [InlineData(1200, 8, 1200)] // eight clicks is a full turn
    public void WrappingCursorTurnsOneDigitAlone(int from, int detents, int expected) =>
        Assert.Equal(expected, Squawk.Step(from, detents, Second));

    [Fact]
    public void EachCursorPicksItsOwnDigit()
    {
        Assert.Equal(2200, Squawk.Step(1200, 1, First));
        Assert.Equal(1300, Squawk.Step(1200, 1, Second));
        Assert.Equal(1210, Squawk.Step(1200, 1, Third));
        Assert.Equal(1201, Squawk.Step(1200, 1, Fourth));
    }

    [Theory]
    [InlineData(77, 1, 100)] // counts in octal: 0077 + 1 = 0100
    [InlineData(100, -1, 77)]
    [InlineData(7776, 5, 7777)] // stops at the top
    [InlineData(1, -5, 0)] // and the bottom
    public void CarryingCursorCountsInOctal(int from, int detents, int expected) =>
        Assert.Equal(expected, Squawk.Step(from, detents, Fourth with { Wrap = CursorWrap.Carry }));

    [Theory]
    [InlineData(7600, 1, 7700)]
    [InlineData(7700, 1, 7700)] // the digit stops at 7
    [InlineData(7000, -1, 7000)] // and at 0
    public void ClampingCursorStopsTheDigit(int from, int detents, int expected) =>
        Assert.Equal(expected, Squawk.Step(from, detents, Second with { Wrap = CursorWrap.Clamp }));

    [Theory]
    [InlineData(7790, 7777)]
    [InlineData(1238, 1237)]
    [InlineData(1239, 1240)]
    [InlineData(-5, 0)]
    [InlineData(99999, 7777)]
    public void SnapsToTheNearestCodeByValue(int value, int expected) => Assert.Equal(expected, Squawk.Snap(value));

    [Fact]
    public void OffGridStartIsSnappedBeforeStepping()
    {
        // 1238 is not a code; it snaps to 1237 and turning the last digit up wraps to 1230.
        Assert.Equal(1230, Squawk.Step(1238, 1, Fourth));
    }

    [Fact]
    public void ACursorThatIsNotAPlaceValueIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Squawk.Step(1200, 1, Fourth with { Step = 8 }));
        Assert.Throws<ArgumentException>(() => Squawk.Step(1200, 1, Fourth with { Step = 10000 }));
    }

    [Fact]
    public void EveryCodeStepsUpAndBackToItself()
    {
        foreach (CursorLevel cursor in (CursorLevel[])[First, Second, Third, Fourth])
        {
            Assert.All(Squawk.Codes, code => Assert.Equal(code, Squawk.Step(Squawk.Step(code, 1, cursor), -1, cursor)));
        }
    }

    [Fact]
    public void EveryStepLandsOnACode()
    {
        CursorLevel carry = Third with { Wrap = CursorWrap.Carry };
        CursorLevel clamp = Third with { Wrap = CursorWrap.Clamp };

        foreach (int detents in (int[])[-100, -9, -1, 1, 7, 100])
        {
            foreach (CursorLevel cursor in (CursorLevel[])[First, Fourth, carry, clamp])
            {
                Assert.All(Squawk.Codes, code => Assert.True(Squawk.Contains(Squawk.Step(code, detents, cursor))));
            }
        }
    }

    [Fact]
    public void OtherRadixesWork()
    {
        // Two decimal digits: 00-99, and a wrapping units digit goes 9 → 0.
        DigitGrid two = new(digits: 2, radix: 10);

        Assert.Equal(100, two.Codes.Count);
        Assert.Equal(40, two.Step(49, 1, Fourth));
    }

    [Fact]
    public void BadConstructionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DigitGrid(digits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DigitGrid(radix: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DigitGrid(radix: 16));
    }
}
