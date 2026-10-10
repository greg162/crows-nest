using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels.Com;
using Crowsnest.Core.Panels.Transponder;

namespace Crowsnest.Core.Tests.Panels.Transponder;

/// <summary>IDENT on a tap, and the mode under the code, through the real engine.</summary>
public class TransponderBehaviourTests
{
    private const string Xpdr = "panel-xpdr";
    private const string Com = "panel-com";
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);

    private static PanelEngine NewEngine()
    {
        PanelSetup setup = PanelComposer.Compose([new ComPanelModule(), new TransponderPanelModule()]);
        PanelEngine engine = new(setup.Registry, setup.Pages, DefaultInputActionMap.Instance, TuningOptions.Default, setup.Behaviours);
        engine.Join(Xpdr, [.. setup.Pages.Where(p => p.Id == TransponderBehaviour.PageId)]);
        engine.Join(Com, [.. setup.Pages.Where(p => p.Id == "com1")]);
        return engine;
    }

    private static DeviceInputEvent.ScreenTapped Tap(long sequence) => new(sequence, T0, 240, 240);

    [Fact]
    public void ATapOnTheTransponderSendsIdent()
    {
        PanelEngine engine = NewEngine();

        PanelEffects effects = engine.OnInput(Xpdr, Tap(1), T0);

        Assert.Equal([new SimCommand.Invoke(TransponderBehaviour.IdentEvent)], effects.Sim);
        Assert.NotNull(effects.FrameFor(Xpdr)); // the tap is acknowledged
    }

    [Fact]
    public void ATapOnAPageWithASwapStillSwapsAndNeverIdents()
    {
        PanelEngine engine = NewEngine();

        PanelEffects effects = engine.OnInput(Com, Tap(1), T0);

        Assert.DoesNotContain(new SimCommand.Invoke(TransponderBehaviour.IdentEvent), effects.Sim);
        Assert.Contains(effects.Sim, c => c is SimCommand.Invoke { EventName: "COM_STBY_RADIO_SWAP" });
    }

    [Fact]
    public void TheModeShowsDimUnderTheCode()
    {
        PanelEngine engine = NewEngine();

        PanelEffects effects = engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4));

        Assert.Equal(new PageCaption("ALT"), effects.FrameFor(Xpdr)!.Page.Caption);
        Assert.Null(effects.FrameFor(Com)); // not showing the transponder, so no redraw
    }

    [Fact]
    public void IdentLightsTheCaptionUntilTheSimEndsIt()
    {
        PanelEngine engine = NewEngine();
        engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4));

        Assert.Equal(
            new PageCaption("ALT   IDENT", Lit: true),
            engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Ident, 1)).FrameFor(Xpdr)!.Page.Caption);
        Assert.Equal(
            new PageCaption("ALT"),
            engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Ident, 0)).FrameFor(Xpdr)!.Page.Caption);
    }

    [Theory]
    [InlineData(0, "OFF")]
    [InlineData(1, "STBY")]
    [InlineData(3, "ON")]
    [InlineData(5, "GND")]
    public void EachModeHasItsName(int mode, string name) =>
        Assert.Equal(name, NewEngine().OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, mode)).FrameFor(Xpdr)!.Page.Caption!.Text);

    [Fact]
    public void AnUnknownOrUnavailableModeShowsNoCaption()
    {
        PanelEngine engine = NewEngine();
        engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4));

        Assert.Null(engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 9)).FrameFor(Xpdr)!.Page.Caption);
        Assert.Null(engine.Render(Xpdr).Page.Caption);

        engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4));
        Assert.Null(engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 0, Available: false)).FrameFor(Xpdr)!.Page.Caption);
    }

    [Fact]
    public void AnUnchangedCaptionDoesNotRedraw()
    {
        PanelEngine engine = NewEngine();
        engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4));

        Assert.Same(PanelEffects.None, engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 4)));
    }

    [Fact]
    public void TheCaptionIsKeptWhileThePilotIsOnAnotherPage()
    {
        PanelSetup setup = PanelComposer.Compose([new ComPanelModule(), new TransponderPanelModule()]);
        PanelEngine engine = new(setup.Registry, setup.Pages, DefaultInputActionMap.Instance, TuningOptions.Default, setup.Behaviours);
        engine.Join(Xpdr); // every page, COM 1 first
        engine.OnSnapshot(new ParameterSnapshot(TransponderBehaviour.Mode, 3));

        while (engine.CurrentPage(Xpdr).Id != TransponderBehaviour.PageId)
        {
            engine.OnInput(Xpdr, new DeviceInputEvent.SwipeDetected(engine.Render(Xpdr).AckSequence + 1, T0, SwipeDir.Left), T0);
        }

        Assert.Equal(new PageCaption("ON"), engine.Render(Xpdr).Page.Caption);
    }

    [Fact]
    public void TheModuleFailsToLoadWithoutItsWatches()
    {
        TransponderBehaviour behaviour = new();
        ParameterRegistry registry = PanelComposer.Compose([new ComPanelModule()]).Registry;

        Assert.Throws<InvalidDataException>(() => behaviour.Validate(registry));
    }
}
