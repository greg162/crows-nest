using System.Text;
using Crowsnest.Core.Application;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;
using Crowsnest.Core.Panels;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests.Application;

public class ParameterJsonReaderTests
{
    private const string Heading = """
        {
          "id": "ap.heading", "label": "HEADING", "group": "ap.hdg", "unit": "deg",
          "grid":    { "type": "wrapping", "min": 0, "max": 359, "step": 1 },
          "cursors": [ { "name": "tens", "step": 10, "span": "0..2" }, { "name": "ones", "step": 1, "span": "2..3" } ],
          "read":    { "source": "simvar", "name": "AUTOPILOT HEADING LOCK DIR", "unit": "degrees" },
          "write":   { "mode": "keyEvent", "target": "HEADING_BUG_SET", "encoding": "raw" },
          "format":  "deg3"
        }
        """;

    private static IReadOnlyList<ParameterDefinition> Read(string json) =>
        DefaultParameters.CreateReader(new ComSpacingBehaviour()).Read(new MemoryStream(Encoding.UTF8.GetBytes(json))).Parameters;

    /// <summary>The heading entry with one fragment replaced, expecting the read to fail naming <paramref name="expected"/>.</summary>
    private static void AssertRejected(string find, string replace, string expected)
    {
        Assert.Contains(find, Heading, StringComparison.Ordinal);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Read($"[{Heading.Replace(find, replace, StringComparison.Ordinal)}]"));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithOptionalFieldsLeftOutReadsWithDefaults()
    {
        ParameterDefinition heading = Assert.Single(Read($"[{Heading}]"));

        Assert.IsType<WrappingGrid>(heading.Grid);
        Assert.All(heading.Cursors, c => Assert.Equal(CursorWrap.Carry, c.Wrap)); // wrap defaults to carry
        Assert.Equal(1, heading.Read.Scale); // scale defaults to 1
        Assert.Equal(CanonicalUnit.Degrees, heading.Unit);
        Assert.Equal("005", heading.Formatter.Format(5));
        Assert.Equal(5, heading.Grid.Step(355, 1, heading.Cursors[0]));
    }

    [Fact]
    public void CommentsTrailingCommasAndEnumCaseAreTolerated()
    {
        string json = $"""
            // a comment
            [ {Heading.Replace("\"keyEvent\"", "\"KEYEVENT\"", StringComparison.Ordinal)}, ]
            """;

        Assert.Equal(WriteMode.KeyEvent, Assert.Single(Read(json)).Write.Mode);
    }

    [Fact]
    public void FromEndSpansAreRead()
    {
        string altitude = """
            [{ "id": "ap.altitude", "label": "ALTITUDE", "group": "ap.alt", "unit": "ft",
               "grid": { "type": "linear", "min": 0, "max": 50000, "step": 100 },
               "cursors": [ { "name": "coarse", "step": 1000, "span": "..^4" }, { "name": "fine", "step": 100, "span": "^3..^2" } ],
               "read": { "source": "simvar", "name": "AUTOPILOT ALTITUDE LOCK VAR", "unit": "feet" },
               "write": { "mode": "keyEvent", "target": "AP_ALT_VAR_SET_ENGLISH", "encoding": "raw" },
               "format": "thousands" }]
            """;

        ParameterDefinition parameter = Assert.Single(Read(altitude));

        Assert.Equal(..^4, parameter.Cursors[0].DisplaySpan);
        Assert.Equal(^3..^2, parameter.Cursors[1].DisplaySpan);
    }

    [Theory]
    [InlineData("\"label\": \"HEADING\", ", "", "\"label\" is required")]
    [InlineData("\"unit\": \"deg\"", "\"unit\": \"radians\"", "\"unit\" is \"radians\"")]
    [InlineData("\"type\": \"wrapping\"", "\"type\": \"spiral\"", "no grid type \"spiral\"")]
    [InlineData("\"max\": 359, ", "", "needs an integer \"max\"")]
    [InlineData("\"step\": 1 }", "\"step\": \"one\" }", "\"step\" must be an integer")]
    [InlineData("\"step\": 1 }", "\"step\": 7 }", "the wrapping grid is invalid")] // 360° is not a whole number of 7° steps
    [InlineData("\"span\": \"0..2\"", "\"span\": \"zero to two\"", "cursor 'tens' needs a \"span\"")]
    [InlineData("\"name\": \"tens\", \"step\": 10, ", "\"name\": \"tens\", \"step\": 10, \"wrap\": \"sideways\", ", "cursor 'tens' wrap")]
    [InlineData("\"name\": \"tens\", \"step\": 10, ", "\"name\": \"tens\", \"step\": 10, \"wrap\": \"clamp\", ", "cursor 'tens' does not fit the wrapping grid")]
    [InlineData("\"name\": \"ones\"", "\"name\": \"tens\"", "two cursors share a name")]
    [InlineData("\"source\": \"simvar\"", "\"source\": \"telepathy\"", "\"read.source\" is \"telepathy\"")]
    [InlineData("\"encoding\": \"raw\"", "\"encoding\": \"morse\"", "\"write.encoding\" is \"morse\"")]
    [InlineData("\"format\":  \"deg3\"", "\"format\":  \"roman\"", "no formatter \"roman\"")]
    public void ABadFieldIsRejectedNamingTheEntryAndTheField(string find, string replace, string expected)
    {
        AssertRejected(find, replace, "Parameter 'ap.heading'");
        AssertRejected(find, replace, expected);
    }

    [Fact]
    public void AnEntryWithNoIdIsReportedByPosition()
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Read("""[{ "label": "X" }]"""));

        Assert.Contains("entry 0", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ \"id\": \"not an array\" }")]
    [InlineData("[ { \"id\": ")]
    public void JsonThatIsNotAnArrayOfEntriesIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Read(json));

    private static ParameterFile ReadFile(string json) =>
        DefaultParameters.CreateReader(new ComSpacingBehaviour()).Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void AWatchEntryIsReadWithOnlyAnIdAndABinding()
    {
        ParameterFile file = ReadFile("""
            [{ "kind": "watch", "id": "com1.spacing", "read": { "source": "simvar", "name": "COM SPACING MODE:1", "unit": "Enum" } }]
            """);

        Assert.Empty(file.Parameters);
        Assert.Equal(new SimSubscription(new ParameterId("com1.spacing"), new ReadBinding(ReadSource.SimVar, "COM SPACING MODE:1", "Enum", 1)), Assert.Single(file.Watches));
    }

    [Theory]
    [InlineData("""[{ "kind": "watch", "id": "w", "read": { "source": "simvar", "name": "X", "unit": "Enum" }, "format": "freq3" }]""", "a watch is only read")]
    [InlineData("""[{ "kind": "watch", "id": "w" }]""", "Watch 'w': \"read\" is required")]
    [InlineData("""[{ "kind": "gizmo", "id": "w" }]""", "\"kind\" is \"gizmo\"")]
    public void ABadWatchIsRejected(string json, string expected)
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ReadFile(json));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }
}
