using System.Text;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Tests.Application;

/// <summary>The rules a panel module is held to (spec §5.8), with modules and files made up in the test.</summary>
public class PanelComposerTests
{
    private sealed class TestModule(string id, Action<PanelBuilder>? configure = null) : IPanelModule
    {
        public string Id => id;

        public void Configure(PanelBuilder panel) => configure?.Invoke(panel);
    }

    private sealed class RecordingBehaviour : IPanelBehaviour
    {
        public ParameterRegistry? ValidatedWith { get; private set; }

        public void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context)
        {
        }

        public void Validate(ParameterRegistry registry) => ValidatedWith = registry;
    }

    private static string Parameter(string id, string grid = """{ "type": "linear", "min": 0, "max": 100, "step": 1 }""", string format = "deg3") => $$"""
        { "id": "{{id}}", "label": "X", "group": "x", "unit": "deg", "grid": {{grid}},
          "cursors": [ { "name": "ones", "step": 1, "span": "0..3" } ],
          "read": { "source": "simvar", "name": "X", "unit": "degrees" },
          "write": { "mode": "keyEvent", "target": "X_SET", "encoding": "raw" },
          "format": "{{format}}" }
        """;

    private static string Parameters(params string[] entries) => $"[{string.Join(',', entries)}]";

    private static string View(string pageId, params string[] fields) =>
        $$"""{ "pages": [ { "id": "{{pageId}}", "title": "T", "layout": "single", "fields": [ {{string.Join(", ", fields.Select(f => $"\"{f}\""))}} ] } ] }""";

    private static PanelFile File(string name, string json) => new(name, () => new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static PanelSetup Compose(params (IPanelModule Module, PanelFile[] Files)[] modules) =>
        PanelComposer.Compose([.. modules.Select(m => m.Module)], module => modules.Single(m => m.Module == module).Files);

    private static InvalidDataException Rejected(params (IPanelModule Module, PanelFile[] Files)[] modules) =>
        Assert.Throws<InvalidDataException>(() => Compose(modules));

    [Fact]
    public void ModulesContributeTheirParametersAndPagesInCatalogOrder()
    {
        PanelSetup setup = Compose(
            (new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))), File("View.hdg.view.json", View("hdg", "hdg.bug"))]),
            (new TestModule("alt"), [File("alt.parameters.json", Parameters(Parameter("alt.bug"))), File("View.alt.view.json", View("alt", "alt.bug"))]));

        Assert.Equal(["hdg.bug", "alt.bug"], setup.Registry.All.Select(p => p.Id.Key));
        Assert.Equal(["hdg", "alt"], setup.Pages.Select(p => p.Id));
    }

    [Fact]
    public void AModulesOwnGridTypeAndFormatterAreAvailableToItsJson()
    {
        PanelSetup setup = Compose((
            new TestModule("hdg", panel => panel
                .AddGridType("tenths", _ => new LinearGrid(0, 10, 1))
                .AddFormatter("plain", new PaddedFormatter(digits: 2))),
            [File("hdg.parameters.json", Parameters(Parameter("hdg.bug", """{ "type": "tenths" }""", "plain")))]));

        Assert.True(setup.Registry[new ParameterId("hdg.bug")].Grid.Contains(10));
    }

    [Fact]
    public void AnotherModulesGridTypeIsNotAvailable()
    {
        InvalidDataException e = Rejected(
            (new TestModule("hdg", panel => panel.AddGridType("tenths", _ => new LinearGrid(0, 10, 1))), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug")))]),
            (new TestModule("alt"), [File("alt.parameters.json", Parameters(Parameter("alt.bug", """{ "type": "tenths" }""")))]));

        Assert.Contains("Panel 'alt', alt.parameters.json", e.Message, StringComparison.Ordinal);
        Assert.Contains("tenths", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleCannotReplaceAStandardGridType() =>
        Assert.Throws<InvalidOperationException>(() => Compose((
            new TestModule("hdg", panel => panel.AddGridType("linear", _ => new LinearGrid(0, 10, 1))),
            [File("hdg.parameters.json", Parameters(Parameter("hdg.bug")))])));

    [Theory]
    [InlineData("alt.bug")]
    [InlineData("x.hdg")]
    public void AParameterIdMustStartWithThePanelId(string id)
    {
        InvalidDataException e = Rejected((new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter(id)))]));

        Assert.Contains($"'{id}' must start with 'hdg'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APageMayNotShowAnotherPanelsParameter()
    {
        InvalidDataException e = Rejected(
            (new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug")))]),
            (new TestModule("alt"), [File("alt.parameters.json", Parameters(Parameter("alt.bug"))), File("View.alt.view.json", View("alt", "hdg.bug"))]));

        Assert.Contains("page 'alt' shows 'hdg.bug'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APageIdMustStartWithThePanelId()
    {
        InvalidDataException e = Rejected((new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))), File("View.v.view.json", View("heading", "hdg.bug"))]));

        Assert.Contains("page 'heading' must start with 'hdg'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwapEventNeedsExactlyTwoFields()
    {
        const string view = """{ "pages": [ { "id": "hdg", "title": "T", "layout": "single", "fields": [ "hdg.bug" ], "swapEvent": "HDG_SWAP" } ] }""";

        InvalidDataException e = Rejected((new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))), File("View.hdg.view.json", view)]));

        Assert.Contains("exactly two fields", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfAnUnknownKindIsRejectedByName()
    {
        InvalidDataException e = Rejected((new TestModule("hdg"), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))), File("hdg.settings.json", "{}")]));

        Assert.Contains("Panel 'hdg', hdg.settings.json", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatFindsNoFilesSaysWhereItLooked()
    {
        InvalidDataException e = Rejected((new TestModule("hdg"), []));

        Assert.Contains("namespace must match its folder", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Com")]
    [InlineData("1com")]
    [InlineData("com.one")]
    public void APanelIdIsLowerCaseLettersAndDigits(string id) =>
        Rejected((new TestModule(id), [File("x.parameters.json", Parameters(Parameter($"{id}.x")))]));

    [Fact]
    public void TwoPanelsCannotShareAnId()
    {
        PanelFile[] files = [File("hdg.parameters.json", Parameters(Parameter("hdg.bug")))];

        InvalidDataException e = Rejected((new TestModule("hdg"), files), (new TestModule("hdg"), files));

        Assert.Contains("Two panels have the id 'hdg'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DemoValuesAreCollected()
    {
        PanelSetup setup = Compose((new TestModule("hdg"), [
            File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))),
            File("hdg.demo.json", """{ "hdg.bug": 42 } // comments are fine"""),
        ]));

        Assert.Equal(42, setup.DemoValues[new ParameterId("hdg.bug")]);
    }

    [Fact]
    public void ADemoValueForAnIdThePanelDoesNotHaveIsRejected()
    {
        InvalidDataException e = Rejected((new TestModule("hdg"), [
            File("hdg.parameters.json", Parameters(Parameter("hdg.bug"))),
            File("hdg.demo.json", """{ "hdg.bgu": 42 }"""),
        ]));

        Assert.Contains("'hdg.bgu' is not one of this panel's", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BehavioursAreValidatedAgainstTheFinishedRegistry()
    {
        RecordingBehaviour behaviour = new();

        PanelSetup setup = Compose(
            (new TestModule("hdg", panel => panel.AddBehaviour(behaviour)), [File("hdg.parameters.json", Parameters(Parameter("hdg.bug")))]),
            (new TestModule("alt"), [File("alt.parameters.json", Parameters(Parameter("alt.bug")))]));

        Assert.Same(setup.Registry, behaviour.ValidatedWith);
        Assert.Equal([behaviour], setup.Behaviours);
    }
}
