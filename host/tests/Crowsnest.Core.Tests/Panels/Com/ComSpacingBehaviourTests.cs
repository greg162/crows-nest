using System.Text;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests.Panels.Com;

/// <summary>COM grids follow COM SPACING MODE (spec §5.3), end to end through the engine.</summary>
public class ComSpacingBehaviourTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly ParameterId Standby = new("com1.standby");
    private static readonly ParameterId Active = new("com1.active");
    private static readonly ParameterId Spacing = new("com1.spacing");

    private long _sequence;

    /// <summary>The shipped panel with the sim reporting 121.500 / 118.000, cursor on kHz.</summary>
    private PanelEngine Tuned()
    {
        PanelSetup setup = DefaultParameters.Load();
        PanelEngine engine = new(setup.Registry, setup.Pages, DefaultInputActionMap.Instance, TuningOptions.Default, setup.Behaviours);
        engine.OnSnapshot(new ParameterSnapshot(Standby, 121_500));
        engine.OnSnapshot(new ParameterSnapshot(Active, 118_000));
        engine.OnInput(new DeviceInputEvent.KnobPressed(++_sequence, T0, PressKind.Short), T0);
        return engine;
    }

    private PanelEffects Turn(PanelEngine engine, int detents) =>
        engine.OnInput(new DeviceInputEvent.EncoderTurned(++_sequence, T0, detents), T0);

    private static ComChannelGrid GridOf(PanelEngine engine, ParameterId id) => (ComChannelGrid)engine.Session(id).Parameter.Grid;

    [Fact]
    public void BeforeTheSimReportsAModeTheGridIs25Khz()
    {
        PanelEngine engine = Tuned();

        Assert.Equal("121.525", Turn(engine, 1).Frame!.Fields[0].Text);
    }

    [Fact]
    public void EightThreeModeLetsTheKnobReachEveryChannelName()
    {
        PanelEngine engine = Tuned();

        engine.OnSnapshot(new ParameterSnapshot(Spacing, 1));

        Assert.Equal("121.505", Turn(engine, 1).Frame!.Fields[0].Text);
        Assert.Equal(ChannelSpacing.EightPointThreeThree, GridOf(engine, Active).Spacing); // both radios follow
    }

    [Fact]
    public void BackTo25KhzModeRestoresTheQuarterSteps()
    {
        PanelEngine engine = Tuned();
        engine.OnSnapshot(new ParameterSnapshot(Spacing, 1));

        engine.OnSnapshot(new ParameterSnapshot(Spacing, 0));

        Assert.Equal(ChannelSpacing.TwentyFiveKhz, GridOf(engine, Standby).Spacing);
        Assert.Equal("121.525", Turn(engine, 1).Frame!.Fields[0].Text);
    }

    [Fact]
    public void AModeChangeMidEditDropsTheDialledValueAndRedraws()
    {
        PanelEngine engine = Tuned();
        engine.OnSnapshot(new ParameterSnapshot(Spacing, 1));
        Turn(engine, 1); // 121.505: an 8.33-only name

        DisplayFrame frame = engine.OnSnapshot(new ParameterSnapshot(Spacing, 0)).Frame!;

        Assert.Equal("121.500", frame.Fields[0].Text);
        Assert.False(frame.Fields[0].Pending);
        Assert.Empty(engine.OnTick(T0.AddSeconds(1)).Sim); // the illegal value is never written
    }

    [Fact]
    public void TheSimsOwnSnapAfterAModeChangeArrivesAsAnOrdinaryValue()
    {
        PanelEngine engine = Tuned();
        engine.OnSnapshot(new ParameterSnapshot(Spacing, 1));
        engine.OnSnapshot(new ParameterSnapshot(Standby, 121_505));

        engine.OnSnapshot(new ParameterSnapshot(Spacing, 0));
        DisplayFrame snapped = engine.OnSnapshot(new ParameterSnapshot(Standby, 121_500)).Frame!;

        Assert.Equal("121.500", snapped.Fields[0].Text);
    }

    [Fact]
    public void AModeChangeWithNothingDialledNeedsNoRedraw()
    {
        PanelEngine engine = Tuned();

        Assert.Same(PanelEffects.None, engine.OnSnapshot(new ParameterSnapshot(Spacing, 1)));
    }

    [Theory]
    [InlineData(2, true)] // not a mode we know
    [InlineData(1, false)] // unavailable
    public void UnknownOrUnavailableModesLeaveTheGridAlone(int mode, bool available)
    {
        PanelEngine engine = Tuned();

        engine.OnSnapshot(new ParameterSnapshot(Spacing, mode, available));

        Assert.Equal(ChannelSpacing.TwentyFiveKhz, GridOf(engine, Standby).Spacing);
    }

    [Fact]
    public void AGridFollowingAWatchThatDoesNotExistFailsAtStartup()
    {
        const string json = """
            [{ "id": "com1.standby", "label": "COM 1 STBY", "group": "com1", "unit": "kHz",
               "grid": { "type": "comChannel", "spacingFrom": "com1.spacnig" },
               "cursors": [ { "name": "khz", "step": 1, "span": "4..7" } ],
               "read": { "source": "simvar", "name": "COM STANDBY FREQUENCY:1", "unit": "Hz", "scale": 0.001 },
               "write": { "mode": "keyEvent", "target": "COM_STBY_RADIO_SET_HZ", "encoding": "hz" },
               "format": "freq3" }]
            """;
        ComSpacingBehaviour spacing = new();
        ParameterFile file = DefaultParameters.CreateReader(spacing).Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => spacing.Validate(new ParameterRegistry(file.Parameters, file.Watches)));

        Assert.Contains("com1.spacnig", e.Message, StringComparison.Ordinal);
    }
}
