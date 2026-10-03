using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Tests.Domain.Formatting;

public class CursorSpansTests
{
    [Fact]
    public void AStartRelativeSpanIsUnchanged()
    {
        // The kHz digits of "121.500", from the com1.standby registry entry.
        Assert.Equal(4..7, CursorSpans.Resolve(4..7, "121.500".Length));
    }

    [Theory]
    [InlineData("12,000", 0, 2)] // the spec's §6 example frame: cursor [0,2]
    [InlineData("9,000", 0, 1)]
    [InlineData("500", 0, 0)] // no thousands to underline
    public void AnEndRelativeSpanFollowsTheWidthOfTheValue(string text, int start, int end)
    {
        bool fits = CursorSpans.TryResolve(..^4, text.Length, out Range resolved);

        if (start == end)
        {
            Assert.False(fits);
        }
        else
        {
            Assert.True(fits);
            Assert.Equal(start..end, resolved);
        }
    }

    [Theory]
    [InlineData("12,000", 3)]
    [InlineData("9,000", 2)]
    [InlineData("500", 0)]
    public void TheHundredsDigitIsFoundFromTheEnd(string text, int start)
    {
        Range resolved = CursorSpans.Resolve(^3..^2, text.Length);

        Assert.Equal(start..(start + 1), resolved);
        Assert.Equal(text[resolved], text[start].ToString());
    }

    [Fact]
    public void ASpanThatDoesNotFitIsRejected()
    {
        Assert.False(CursorSpans.TryResolve(4..8, 7, out _));
        Assert.False(CursorSpans.TryResolve(^8..^0, 7, out _));
        Assert.False(CursorSpans.TryResolve(3..3, 7, out _)); // empty underlines nothing
        Assert.Throws<ArgumentOutOfRangeException>(() => CursorSpans.Resolve(4..8, 7));
    }

    [Theory]
    [InlineData("4..7", 4, false, 7, false)]
    [InlineData("0..3", 0, false, 3, false)]
    [InlineData("..^4", 0, false, 4, true)]
    [InlineData("^3..^2", 3, true, 2, true)]
    [InlineData("2..", 2, false, 0, true)]
    public void SpansParseFromRegistrySyntax(string text, int start, bool startFromEnd, int end, bool endFromEnd)
    {
        Assert.True(CursorSpans.TryParse(text, out Range span));
        Assert.Equal(new Range(new Index(start, startFromEnd), new Index(end, endFromEnd)), span);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("4...7")]
    [InlineData("4..7..9")]
    [InlineData("-1..3")]
    [InlineData(" 4..7")]
    [InlineData("^..3")]
    [InlineData("a..b")]
    public void MalformedSpansDoNotParse(string? text) => Assert.False(CursorSpans.TryParse(text, out _));
}
