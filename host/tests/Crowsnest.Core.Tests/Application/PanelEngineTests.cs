using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;

namespace Crowsnest.Core.Tests.Application;

/// <summary>Scenario tests for the panel's behaviour, with every timestamp explicit (spec §11).</summary>
public class PanelEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");
    private static readonly ParameterId NavStandby = new("nav1.standby");
    private static readonly ParameterId NavActive = new("nav1.active");

    private static readonly PanelPage NavPage = new("nav1", "NAV 1", PageLayout.ActiveStandbyPair, [NavStandby, NavActive], "NAV1_RADIO_SWAP");

    /// <summary>The panel these tests drive; the multi-panel tests add <see cref="Q"/>.</summary>
    private const string P = "panel-a";
    private const string Q = "panel-b";

    private long _sequence;

    private static DateTimeOffset At(int ms) => T0 + TimeSpan.FromMilliseconds(ms);

    private static PanelEngine NewEngine(TuningOptions? options = null)
    {
        // COM 1 and a NAV page: two pages, whatever else the shipped views add.
        PanelSetup setup = DefaultParameters.Load();
        PanelEngine engine = new(setup.Registry, [setup.Pages.Single(p => p.Id == "com1"), NavPage], DefaultInputActionMap.Instance, options ?? TuningOptions.Default, setup.Behaviours);
        engine.Join(P);
        return engine;
    }

    /// <summary>An engine with the sim connected and reporting COM 1 at 121.500 / 118.000.</summary>
    private static PanelEngine Tuned(TuningOptions? options = null)
    {
        PanelEngine engine = NewEngine(options);
        engine.OnSimConnection(SimConnectionState.Connected);
        engine.OnSnapshot(new ParameterSnapshot(ComStandby, 121_500));
        engine.OnSnapshot(new ParameterSnapshot(ComActive, 118_000));
        return engine;
    }

    private DeviceInputEvent Turn(int detents, int ms) => new DeviceInputEvent.EncoderTurned(++_sequence, At(ms), detents);

    private DeviceInputEvent Press(PressKind kind, int ms = 0) => new DeviceInputEvent.KnobPressed(++_sequence, At(ms), kind);

    private DeviceInputEvent Tap(int ms = 0) => new DeviceInputEvent.ScreenTapped(++_sequence, At(ms), 240, 240);

    private DeviceInputEvent Swipe(SwipeDir dir) => new DeviceInputEvent.SwipeDetected(++_sequence, At(0), dir);

    [Fact]
    public void BeforeTheSimReportsTheFrameShowsPlaceholdersAndNoCursor()
    {
        DisplayFrame frame = NewEngine().Render(P);

        Assert.Equal(SimConnectionState.Disconnected, frame.Sim);
        Assert.Equal(new PageDescriptor("com1", "COM1", PageLayout.ActiveStandbyPair, 0, 2), frame.Page);
        Assert.Equal(["---.---", "---.---"], frame.Fields.Select(f => f.Text));
        Assert.Equal([FieldRole.Primary, FieldRole.Secondary], frame.Fields.Select(f => f.Role));
        Assert.All(frame.Fields, f => Assert.Null(f.CursorSpan));
    }

    [Fact]
    public void SimValuesAppearWithTheCursorOnTheFirstLevel()
    {
        DisplayFrame frame = Tuned().Render(P);

        Assert.Equal(SimConnectionState.Connected, frame.Sim);
        Assert.Equal(new FieldDescriptor(FieldRole.Primary, "COM 1 STBY", "121.500", 0..3, Pending: false), frame.Fields[0]);
        Assert.Equal(new FieldDescriptor(FieldRole.Secondary, "COM 1", "118.000", null, Pending: false), frame.Fields[1]);
    }

    [Fact]
    public void ASnapshotForAParameterOffScreenDoesNotRedrawButIsShownWhenItsPageIs()
    {
        PanelEngine engine = Tuned();

        Assert.Null(engine.OnSnapshot(new ParameterSnapshot(NavStandby, 110_350)).FrameFor(P));

        DisplayFrame nav = engine.OnInput(P, Swipe(SwipeDir.Left), At(0)).FrameFor(P)!;

        Assert.Equal("nav1", nav.Page.Id);
        Assert.Equal("110.35", nav.Fields[0].Text);
    }

    [Fact]
    public void TurningTheKnobShowsThePendingValueAtOnceAndWritesAfterTheDebounce()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Press(PressKind.Short), At(0)); // cursor to kHz

        PanelEffects turned = engine.OnInput(P, Turn(1, 10), At(10));

        Assert.Empty(turned.Sim);
        Assert.Equal("121.525", turned.FrameFor(P)!.Fields[0].Text);
        Assert.True(turned.FrameFor(P)!.Fields[0].Pending);
        Assert.Equal(4..7, turned.FrameFor(P)!.Fields[0].CursorSpan);
        Assert.Equal(_sequence, turned.FrameFor(P)!.AckSequence);

        Assert.Same(PanelEffects.None, engine.OnTick(At(100)));

        PanelEffects written = engine.OnTick(At(130));

        Assert.Equal([new SimCommand.Write(ComStandby, 121_525)], written.Sim);
        Assert.Null(written.FrameFor(P)); // a write changes nothing on screen
    }

    [Fact]
    public void TheSimConfirmingClearsThePendingMark()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));
        engine.OnTick(At(120));

        DisplayFrame confirmed = engine.OnSnapshot(new ParameterSnapshot(ComStandby, 122_500)).FrameFor(P)!;

        Assert.Equal("122.500", confirmed.Fields[0].Text);
        Assert.False(confirmed.Fields[0].Pending);
    }

    [Fact]
    public void AnUnconfirmedWriteIsRejectedOnATickAndTheSimsValueShown()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));
        engine.OnTick(At(120));

        DisplayFrame rejected = engine.OnTick(At(1_620)).FrameFor(P)!;

        Assert.Equal("121.500", rejected.Fields[0].Text);
        Assert.False(rejected.Fields[0].Pending);
        Assert.Equal(PendingWriteStatus.Rejected, engine.Session(ComStandby).Status);
    }

    [Fact]
    public void ShortPressCyclesTheCursor()
    {
        PanelEngine engine = Tuned();

        Assert.Equal(4..7, engine.OnInput(P, Press(PressKind.Short), At(0)).FrameFor(P)!.Fields[0].CursorSpan);
        Assert.Equal(0..3, engine.OnInput(P, Press(PressKind.Short), At(0)).FrameFor(P)!.Fields[0].CursorSpan);
    }

    [Fact]
    public void TapSwapsAndSendsADialledValueFirst()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));

        // 50 ms later, inside the debounce: 122.500 has not gone to the sim yet.
        PanelEffects swapped = engine.OnInput(P, Tap(50), At(50));

        Assert.Equal([new SimCommand.Write(ComStandby, 122_500), new SimCommand.Invoke("COM_STBY_RADIO_SWAP")], swapped.Sim);
        Assert.Empty(engine.OnTick(At(500)).Sim); // and not again after
    }

    [Fact]
    public void TapWithNothingDialledJustSwaps()
    {
        Assert.Equal([new SimCommand.Invoke("COM_STBY_RADIO_SWAP")], Tuned().OnInput(P, Tap(), At(0)).Sim);
    }

    [Fact]
    public void TheSwapItselfArrivesAsSnapshotsLikeAnyOtherChange()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Tap(), At(0));

        engine.OnSnapshot(new ParameterSnapshot(ComStandby, 118_000));
        DisplayFrame frame = engine.OnSnapshot(new ParameterSnapshot(ComActive, 121_500)).FrameFor(P)!;

        Assert.Equal(["118.000", "121.500"], frame.Fields.Select(f => f.Text));
    }

    [Fact]
    public void ASwapRightAfterTuningShowsTheSwappedValueEvenIfTheWriteIsNeverConfirmed()
    {
        // The sim can apply the write and the swap in one frame, so the standby's only report is
        // the old active value. It must show at once, not after the settle timeout as Rejected.
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0)); // 121.500 -> 122.500, pending
        engine.OnInput(P, Tap(), At(10));

        DisplayFrame frame = engine.OnSnapshot(new ParameterSnapshot(ComStandby, 118_000)).FrameFor(P)!;

        Assert.Equal("118.000", frame.Fields[0].Text);
        Assert.False(frame.Fields[0].Pending);
        Assert.Equal(PendingWriteStatus.None, engine.Session(ComStandby).Status);
    }

    [Fact]
    public void ASwapRightAfterTuningAlsoHandlesTheWriteConfirmingFirst()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));
        engine.OnInput(P, Tap(), At(10));

        engine.OnSnapshot(new ParameterSnapshot(ComStandby, 122_500)); // the write, one frame early
        DisplayFrame frame = engine.OnSnapshot(new ParameterSnapshot(ComStandby, 118_000)).FrameFor(P)!;

        Assert.Equal("118.000", frame.Fields[0].Text);
        Assert.False(frame.Fields[0].Pending);
    }

    [Fact]
    public void ASwapTheSimNeverReportsStillTimesOut()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));
        engine.OnInput(P, Tap(), At(10));

        engine.OnTick(At(10) + TuningOptions.Default.SettleTimeout + TimeSpan.FromMilliseconds(1));

        Assert.Equal(121_500, engine.Session(ComStandby).Displayed);
        Assert.Equal(PendingWriteStatus.Rejected, engine.Session(ComStandby).Status);
    }

    [Fact]
    public void APanelThatRejoinsHasItsInputsAcknowledgedFromOneAgain()
    {
        PanelEngine engine = Tuned();
        _sequence = 40;
        engine.OnInput(P, Tap(), At(0)); // seq 41

        engine.OnPanelRestarted(P); // the panel restarted and counts from 1
        _sequence = 0;
        PanelEffects tapped = engine.OnInput(P, Tap(), At(10));

        Assert.Equal(1, tapped.FrameFor(P)!.AckSequence);
    }

    [Fact]
    public void TapOnAPageWithNoSwapEventOnlyAcknowledges()
    {
        PanelPage single = new("stby", "STBY", PageLayout.SingleValue, [ComStandby]);
        PanelEngine engine = new(DefaultParameters.Load().Registry, [single], DefaultInputActionMap.Instance, TuningOptions.Default);
        engine.Join(P);

        PanelEffects tapped = engine.OnInput(P, Tap(), At(0));

        Assert.Empty(tapped.Sim);
        Assert.Equal(_sequence, tapped.FrameFor(P)!.AckSequence);
    }

    [Fact]
    public void LongPressAndSwipesMoveBetweenPagesAndWrap()
    {
        PanelEngine engine = Tuned();

        Assert.Equal("nav1", engine.OnInput(P, Press(PressKind.Long), At(0)).FrameFor(P)!.Page.Id);
        Assert.Equal("com1", engine.OnInput(P, Swipe(SwipeDir.Left), At(0)).FrameFor(P)!.Page.Id); // wraps
        Assert.Equal("nav1", engine.OnInput(P, Swipe(SwipeDir.Right), At(0)).FrameFor(P)!.Page.Id);
        Assert.Equal(NavPage, engine.CurrentPage(P));
    }

    [Fact]
    public void TheKnobTunesTheFirstFieldOfWhicheverPageIsShowing()
    {
        PanelEngine engine = Tuned();
        engine.OnSnapshot(new ParameterSnapshot(NavStandby, 110_300));
        engine.OnInput(P, Swipe(SwipeDir.Left), At(0));
        engine.OnInput(P, Press(PressKind.Short), At(0)); // NAV cursor to kHz

        Assert.Equal("110.35", engine.OnInput(P, Turn(1, 0), At(0)).FrameFor(P)!.Fields[0].Text);
        Assert.Equal(121_500, engine.Session(ComStandby).Displayed);
    }

    [Fact]
    public void AWriteStillGoesOutAfterLeavingThePage()
    {
        // Every session is ticked, not just the current page's: tuning COM and swiping away
        // inside the debounce must not strand the write.
        PanelEngine engine = Tuned();
        engine.OnInput(P, Turn(1, 0), At(0));
        engine.OnInput(P, Swipe(SwipeDir.Left), At(50));

        PanelEffects ticked = engine.OnTick(At(120));

        Assert.Equal([new SimCommand.Write(ComStandby, 122_500)], ticked.Sim);
        Assert.Null(ticked.FrameFor(P)); // COM is not on screen
    }

    [Fact]
    public void ASimConnectionChangeRedrawsOnlyWhenItChanges()
    {
        PanelEngine engine = NewEngine();

        Assert.Equal(SimConnectionState.Connecting, engine.OnSimConnection(SimConnectionState.Connecting).FrameFor(P)!.Sim);
        Assert.Same(PanelEffects.None, engine.OnSimConnection(SimConnectionState.Connecting));
    }

    [Fact]
    public void UnavailableAndUnknownSnapshotsAreIgnored()
    {
        PanelEngine engine = Tuned();

        Assert.Same(PanelEffects.None, engine.OnSnapshot(new ParameterSnapshot(ComStandby, 0, Available: false)));
        Assert.Same(PanelEffects.None, engine.OnSnapshot(new ParameterSnapshot(new ParameterId("com9.standby"), 121_500)));
        Assert.Equal(121_500, engine.Session(ComStandby).Displayed);
    }

    [Fact]
    public void AnIgnoredGestureIsStillAcknowledgedButAReplayedOneIsNot()
    {
        PanelEngine engine = Tuned();

        PanelEffects swipedUp = engine.OnInput(P, Swipe(SwipeDir.Up), At(0));
        Assert.Equal(_sequence, swipedUp.FrameFor(P)!.AckSequence);

        PanelEffects replayed = engine.OnInput(P, new DeviceInputEvent.SwipeDetected(_sequence, At(0), SwipeDir.Up), At(0));
        Assert.Same(PanelEffects.None, replayed);
    }

    [Fact]
    public void RevisionsOnlyGoUp()
    {
        PanelEngine engine = Tuned();
        long[] revisions = [engine.Render(P).Revision, engine.Render(P).Revision, engine.OnInput(P, Press(PressKind.Short), At(0)).FrameFor(P)!.Revision];

        Assert.Equal(revisions.Order(), revisions);
        Assert.Equal(revisions.Length, revisions.Distinct().Count());
    }

    [Fact]
    public void AccelerationSeesTheTimeBetweenTurnsAsTheDeviceMeasuredIt()
    {
        RecordingAcceleration acceleration = new();
        PanelEngine engine = Tuned(TuningOptions.Default with { Acceleration = acceleration });

        engine.OnInput(P, Turn(1, 1_000), At(0));
        engine.OnInput(P, Turn(1, 1_040), At(5_000)); // host clock far off; device says 40 ms

        Assert.Equal([TimeSpan.MaxValue, TimeSpan.FromMilliseconds(40)], acceleration.Intervals);
    }

    [Fact]
    public void PagesMustShowRegisteredParametersInOneToThreeFields()
    {
        ParameterRegistry registry = DefaultParameters.Load().Registry;
        PanelPage unknown = new("x", "X", PageLayout.SingleValue, [new ParameterId("com9.standby")]);
        PanelPage empty = new("y", "Y", PageLayout.SingleValue, []);
        PanelPage crowded = new("z", "Z", PageLayout.DualValue, [ComStandby, ComActive, NavStandby, NavActive]);

        Assert.Throws<ArgumentException>(() => new PanelEngine(registry, [unknown], DefaultInputActionMap.Instance, TuningOptions.Default));
        Assert.Throws<ArgumentException>(() => new PanelEngine(registry, [empty], DefaultInputActionMap.Instance, TuningOptions.Default));
        Assert.Throws<ArgumentException>(() => new PanelEngine(registry, [crowded], DefaultInputActionMap.Instance, TuningOptions.Default));
        Assert.Throws<ArgumentException>(() => new PanelEngine(registry, [], DefaultInputActionMap.Instance, TuningOptions.Default));
    }

    [Fact]
    public void ATurnOnOnePanelShowsOnEveryPanelShowingTheValue()
    {
        PanelEngine engine = Tuned();
        engine.Join(Q);

        PanelEffects turned = engine.OnInput(P, Turn(1, 0), At(0));

        Assert.Equal([P, Q], turned.Frames.Select(f => f.PanelId).Order());
        DisplayFrame other = turned.FrameFor(Q)!;
        Assert.Equal("122.500", other.Fields[0].Text);
        Assert.True(other.Fields[0].Pending);
        Assert.Equal(0, other.AckSequence); // Q sent nothing; the ack is P's
        Assert.Equal(_sequence, turned.FrameFor(P)!.AckSequence);
    }

    [Fact]
    public void PanelsShareOneSessionSoTheirTurnsAddUp()
    {
        PanelEngine engine = Tuned();
        engine.Join(Q);

        engine.OnInput(P, Turn(1, 0), At(0));
        PanelEffects second = engine.OnInput(Q, Turn(1, 10), At(10));

        Assert.Equal("123.500", second.FrameFor(P)!.Fields[0].Text);
        Assert.Equal([new SimCommand.Write(ComStandby, 123_500)], engine.OnTick(At(200)).Sim);
    }

    [Fact]
    public void PanelsNavigateIndependently()
    {
        PanelEngine engine = Tuned();
        engine.Join(Q);

        PanelEffects paged = engine.OnInput(P, Swipe(SwipeDir.Left), At(0));

        Assert.Equal([P], paged.Frames.Select(f => f.PanelId));
        Assert.Equal("nav1", engine.CurrentPage(P).Id);
        Assert.Equal("com1", engine.CurrentPage(Q).Id);

        // A NAV value redraws only the panel showing NAV, a COM value only the one showing COM.
        Assert.Equal([P], engine.OnSnapshot(new ParameterSnapshot(NavStandby, 110_350)).Frames.Select(f => f.PanelId));
        Assert.Equal([Q], engine.OnSnapshot(new ParameterSnapshot(ComActive, 124_850)).Frames.Select(f => f.PanelId));
    }

    [Fact]
    public void AnAcknowledgementRedrawsOnlyThePanelThatSentTheInput()
    {
        PanelEngine engine = Tuned();
        engine.Join(Q);

        Assert.Equal([Q], engine.OnInput(Q, Swipe(SwipeDir.Up), At(0)).Frames.Select(f => f.PanelId));
    }

    [Fact]
    public void ASimConnectionChangeRedrawsEveryPanel()
    {
        PanelEngine engine = NewEngine();
        engine.Join(Q);

        Assert.Equal([P, Q], engine.OnSimConnection(SimConnectionState.Connected).Frames.Select(f => f.PanelId).Order());
    }

    [Fact]
    public void APanelThatLeftGetsNoFramesAndItsLateInputIsIgnored()
    {
        PanelEngine engine = Tuned();
        engine.Join(Q);
        engine.Leave(Q);

        Assert.Same(PanelEffects.None, engine.OnInput(Q, Turn(1, 0), At(0)));
        Assert.Equal([P], engine.OnSnapshot(new ParameterSnapshot(ComActive, 124_850)).Frames.Select(f => f.PanelId));
        Assert.Equal(121_500, engine.Session(ComStandby).Displayed);
    }

    [Fact]
    public void APanelThatRejoinsStartsOnTheFirstPage()
    {
        PanelEngine engine = Tuned();
        engine.OnInput(P, Swipe(SwipeDir.Left), At(0));
        engine.Leave(P);

        Assert.Equal("com1", engine.Join(P).FrameFor(P)!.Page.Id);
        Assert.Throws<InvalidOperationException>(() => engine.Join(P));
    }

    private sealed class RecordingAcceleration : IEncoderAcceleration
    {
        public List<TimeSpan> Intervals { get; } = [];

        public int Scale(int detents, TimeSpan sinceLastDetent)
        {
            Intervals.Add(sinceLastDetent);
            return detents;
        }
    }
}
