using System.Text;
using Crowsnest.Core.Application;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels;

namespace Crowsnest.Core.Tests.Application;

public class PanelViewReaderTests
{
    private const string Page = """
        { "id": "com1", "title": "COM1", "layout": "pair",
          "fields": [ "com1.standby", "com1.active" ], "swapEvent": "COM_STBY_RADIO_SWAP" }
        """;

    private static IReadOnlyList<PanelPage> Read(string json) =>
        PanelViewReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static void AssertRejected(string find, string replace, string expected)
    {
        Assert.Contains(find, Page, StringComparison.Ordinal);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Read($$"""{ "pages": [ {{Page.Replace(find, replace, StringComparison.Ordinal)}} ] }"""));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APageReadsWithItsLayoutFieldsAndSwapEvent()
    {
        PanelPage page = Assert.Single(Read($$"""{ "pages": [ {{Page}} ] }"""));

        Assert.Equal("com1", page.Id);
        Assert.Equal("COM1", page.Title);
        Assert.Equal(PageLayout.ActiveStandbyPair, page.Layout);
        Assert.Equal([new ParameterId("com1.standby"), new ParameterId("com1.active")], page.Fields);
        Assert.Equal("COM_STBY_RADIO_SWAP", page.SwapEvent);
    }

    [Fact]
    public void TheSwapEventIsOptionalAndCommentsAreAllowed()
    {
        PanelPage page = Assert.Single(Read("""
            // altitude
            { "pages": [ { "id": "ap.alt", "title": "ALT", "layout": "single", "fields": [ "ap.altitude" ], }, ] }
            """));

        Assert.Equal(PageLayout.SingleValue, page.Layout);
        Assert.Null(page.SwapEvent);
    }

    [Theory]
    [InlineData("\"id\": \"com1\"", "\"id\": \"\"", "no \"id\"")]
    [InlineData("\"title\": \"COM1\"", "\"title\": null", "\"title\" is required")]
    [InlineData("\"layout\": \"pair\"", "\"layout\": \"radio\"", "\"layout\" is \"radio\"")]
    [InlineData("[ \"com1.standby\", \"com1.active\" ]", "[]", "\"fields\" must list")]
    [InlineData("\"COM_STBY_RADIO_SWAP\"", "\" \"", "\"swapEvent\" is empty")]
    public void AnIncompletePageIsRejectedByName(string find, string replace, string expected) =>
        AssertRejected(find, replace, expected);

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"pages\": [] }")]
    [InlineData("{ \"pages\": ")]
    public void AFileWithoutPagesIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Read(json));

    [Fact]
    public void TheShippedViewsShowComOneThenComTwo()
    {
        IReadOnlyList<PanelPage> pages = DefaultParameters.Load().Pages;

        Assert.Equal(["com1", "com2"], pages.Select(p => p.Id));
        Assert.All(pages, p => Assert.Equal(PageLayout.ActiveStandbyPair, p.Layout));
        Assert.Equal(["COM_STBY_RADIO_SWAP", "COM2_RADIO_SWAP"], pages.Select(p => p.SwapEvent));
    }
}
