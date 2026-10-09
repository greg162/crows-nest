using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Transponder;

namespace Crowsnest.Core.Tests.Panels.Transponder;

/// <summary>The squawk as the transponder module's JSON sets it up: four digits, each turned on its own.</summary>
public class TransponderPanelTests
{
    private static readonly ParameterDefinition Code =
        PanelComposer.Compose([new TransponderPanelModule()]).Registry[new ParameterId("xpdr.code")];

    [Fact]
    public void TheCodeIsFourOctalDigitsShownAsTheyRead()
    {
        DigitGrid grid = Assert.IsType<DigitGrid>(Code.Grid);
        Assert.Equal((4, 8), (grid.Digits, grid.Radix));
        Assert.Equal("0042", Code.Formatter.Format(42));
    }

    [Theory]
    [InlineData(0, 7000, 1, 0)]    // the first digit wraps 7 → 0
    [InlineData(1, 1200, -1, 1100)]
    [InlineData(2, 7070, 1, 7000)] // the third digit wraps alone; nothing carries
    [InlineData(3, 7000, -1, 7007)]
    public void EachCursorTurnsOneDigit(int cursor, int from, int detents, int expected) =>
        Assert.Equal(expected, Code.Grid.Step(from, detents, Code.Cursors[cursor]));

    [Fact]
    public void EachCursorUnderlinesItsDigit() =>
        Assert.Equal([0..1, 1..2, 2..3, 3..4], Code.Cursors.Select(c => c.DisplaySpan));
}
