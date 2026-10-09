using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;

namespace Crowsnest.Core.Tests.Application;

/// <summary>What a device shows is an assignment of pages to it (spec §6.2).</summary>
public class PanelEngineAssignmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private const string Device = "a4cb8fdccc6c";

    private static readonly PanelSetup Setup = PanelCatalog.Load();

    private static readonly IReadOnlyList<PanelPage> Com = Setup.PagesFor("com");
    private static readonly IReadOnlyList<PanelPage> Nav = Setup.PagesFor("nav");

    private long _sequence;

    private static PanelEngine NewEngine() =>
        new(Setup.Registry, Setup.Pages, DefaultInputActionMap.Instance, TuningOptions.Default, Setup.Behaviours);

    private DeviceInputEvent Turn(int detents) => new DeviceInputEvent.EncoderTurned(++_sequence, T0, detents);

    private DeviceInputEvent LongPress() => new DeviceInputEvent.KnobPressed(++_sequence, T0, PressKind.Long);

    [Fact]
    public void ADeviceGivenNoPagesShowsTheUnassignedScreenWithTheSixItsWaitingScreenShows()
    {
        DisplayFrame frame = NewEngine().Join(Device, []).FrameFor(Device)!;

        Assert.Equal(PanelEngine.UnassignedPageId, frame.Page.Id);
        Assert.Equal("NOT ASSIGNED", frame.Page.Title);
        Assert.Equal(PageLayout.SingleValue, frame.Page.Layout);
        Assert.Equal("dccc6c", Assert.Single(frame.Fields).Text);
    }

    [Fact]
    public void AnUnassignedDeviceHasItsInputsAcknowledgedAndOtherwiseIgnored()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, []);

        PanelEffects turned = engine.OnInput(Device, Turn(3), T0);
        PanelEffects pressed = engine.OnInput(Device, LongPress(), T0);

        Assert.Empty(turned.Sim);
        Assert.Equal(1, turned.FrameFor(Device)!.AckSequence);
        Assert.Equal(PanelEngine.UnassignedPageId, pressed.FrameFor(Device)!.Page.Id);
        Assert.Throws<InvalidOperationException>(() => engine.CurrentPage(Device));
    }

    [Fact]
    public void AnUnassignedDeviceIsNotRedrawnForSimValues()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, []);

        Assert.Empty(engine.OnSnapshot(new ParameterSnapshot(new ParameterId("com1.standby"), 121_500)).Frames);
    }

    [Fact]
    public void ADeviceShowsOnlyItsPanelsPages()
    {
        PanelEngine engine = NewEngine();

        DisplayFrame frame = engine.Join(Device, Nav).FrameFor(Device)!;
        engine.OnInput(Device, LongPress(), T0);

        Assert.Equal("nav1", frame.Page.Id);
        Assert.Equal(2, frame.Page.Count);
        Assert.Equal("nav2", engine.CurrentPage(Device).Id);
        engine.OnInput(Device, LongPress(), T0);
        Assert.Equal("nav1", engine.CurrentPage(Device).Id); // its last page wraps to its first, not to COM
    }

    [Fact]
    public void ADeviceGivenNothingSpecialShowsEveryPage() =>
        Assert.Equal(Setup.Pages.Count, NewEngine().Join(Device).FrameFor(Device)!.Page.Count);

    [Fact]
    public void APageTheEngineDoesNotKnowIsRefused() =>
        Assert.Throws<ArgumentException>(() => NewEngine().Join(Device, [new PanelPage("adf", "ADF", PageLayout.SingleValue, [new ParameterId("com1.active")])]));

    [Fact]
    public void AssigningAnUnassignedDeviceShowsItsFirstPage()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, []);

        DisplayFrame frame = engine.Assign(Device, Com).FrameFor(Device)!;

        Assert.Equal("com1", frame.Page.Id);
        Assert.Equal(["com1", "com2"], engine.AssignedPages(Device));
    }

    [Fact]
    public void ANewPanelStartsAtItsFirstPage()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, Com);
        engine.OnInput(Device, LongPress(), T0); // on COM2

        Assert.Equal("nav1", engine.Assign(Device, Nav).FrameFor(Device)!.Page.Id);
    }

    [Fact]
    public void TheSamePagesAgainChangeNothing()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, Com);

        Assert.Same(PanelEffects.None, engine.Assign(Device, Setup.PagesFor("com")));
    }

    [Fact]
    public void TakingEveryPageAwayShowsTheUnassignedScreen()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Device, Com);

        Assert.Equal(PanelEngine.UnassignedPageId, engine.Assign(Device, []).FrameFor(Device)!.Page.Id);
    }

    [Fact]
    public void PagesForGivesThePanelsPagesInOrder() =>
        Assert.Equal(["nav1", "nav2"], Setup.PagesFor("nav").Select(p => p.Id));

    [Theory]
    [InlineData(null)]
    [InlineData("adf")]
    public void NoPanelOrAnUnknownOneHasNoPages(string? panel) =>
        Assert.Empty(Setup.PagesFor(panel));
}
