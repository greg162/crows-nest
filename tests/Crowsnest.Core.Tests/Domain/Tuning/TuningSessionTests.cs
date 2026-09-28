using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests.Domain.Tuning;

public class TuningSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Click = TimeSpan.FromMilliseconds(50);

    private static readonly CursorLevel Mhz = TestParameters.Mhz;
    private static readonly CursorLevel Khz = TestParameters.Khz;

    private static readonly ParameterDefinition Com1Standby = TestParameters.Com1Standby();

    private static DateTimeOffset At(int ms) => T0 + TimeSpan.FromMilliseconds(ms);

    /// <summary>A session on COM 1 standby with the sim reporting 121.500, cursor on kHz.</summary>
    private static TuningSession Tuned(int khz = 121_500, TuningOptions? options = null)
    {
        TuningSession session = new(Com1Standby, options ?? TuningOptions.Default);
        session.ObserveSimValue(khz);
        session.ToggleCursor();
        return session;
    }

    [Fact]
    public void DetentsAreIgnoredUntilTheSimHasReportedAValue()
    {
        TuningSession session = new(Com1Standby, TuningOptions.Default);

        Assert.False(session.HasValue);
        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.None), session.ApplyDetents(1, Click, At(0)));
        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.None), session.Tick(At(500)));

        TuningOutcome first = session.ObserveSimValue(121_500);

        Assert.True(session.HasValue);
        Assert.True(first.DisplayChanged);
        Assert.Equal(121_500, session.Displayed);
    }

    [Fact]
    public void ADetentShowsAtOnceAndWritesAfterTheDebounce()
    {
        TuningSession session = Tuned();

        TuningOutcome turned = session.ApplyDetents(1, Click, At(0));

        Assert.Equal(new TuningOutcome(true, null, PendingWriteStatus.AwaitingConfirmation), turned);
        Assert.Equal(121_525, session.Displayed);
        Assert.Equal(121_500, session.Confirmed);

        Assert.Null(session.Tick(At(119)).WriteRequest);
        Assert.Equal(121_525, session.Tick(At(120)).WriteRequest);
        Assert.Null(session.Tick(At(200)).WriteRequest); // written once, not again
    }

    [Fact]
    public void QuickDetentsCoalesceIntoOneWriteOfTheLastValue()
    {
        TuningSession session = Tuned();

        session.ApplyDetents(1, Click, At(0));
        session.ApplyDetents(1, Click, At(50));
        session.ApplyDetents(1, Click, At(100));

        Assert.Null(session.Tick(At(170)).WriteRequest); // 70 ms after the last detent
        Assert.Equal(121_575, session.Tick(At(220)).WriteRequest);
    }

    [Fact]
    public void ALongSpinStreamsWritesInsteadOfWaitingForTheKnobToStop()
    {
        TuningSession session = Tuned();
        List<(int Ms, int Khz)> writes = [];

        // A detent every 50 ms for 1.5 s, ticking every 10 ms, then let it settle.
        for (int ms = 0; ms <= 2_000; ms += 10)
        {
            if (ms <= 1_500 && ms % 50 == 0)
            {
                Record(ms, session.ApplyDetents(1, Click, At(ms)));
            }

            Record(ms, session.Tick(At(ms)));
        }

        Assert.True(writes.Count >= 4, $"only {writes.Count} writes during a 1.5 s spin");
        Assert.All(writes.Zip(writes.Skip(1)), pair => Assert.True(pair.Second.Ms - pair.First.Ms <= 350, $"a {pair.Second.Ms - pair.First.Ms} ms gap between writes"));
        Assert.Equal(session.Displayed, writes[^1].Khz); // the final value always goes out

        void Record(int ms, TuningOutcome outcome)
        {
            if (outcome.WriteRequest is { } khz)
            {
                writes.Add((ms, khz));
            }
        }
    }

    [Fact]
    public void TheSimReportingOurValueConfirmsIt()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120));

        TuningOutcome echoed = session.ObserveSimValue(121_525);

        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.Confirmed), echoed);
        Assert.Equal(121_525, session.Confirmed);
        Assert.Equal(121_525, session.Displayed);
        Assert.Equal(PendingWriteStatus.Confirmed, session.Tick(At(5_000)).Status); // no timeout once confirmed
    }

    [Fact]
    public void OtherSimValuesAreIgnoredWhileAWriteIsPending()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120));

        TuningOutcome stale = session.ObserveSimValue(121_500);
        TuningOutcome other = session.ObserveSimValue(124_850);

        Assert.False(stale.DisplayChanged);
        Assert.False(other.DisplayChanged);
        Assert.Equal(121_525, session.Displayed);
        Assert.Equal(PendingWriteStatus.AwaitingConfirmation, session.Status);
    }

    [Fact]
    public void AnEarlierWriteBeingConfirmedMidSpinDoesNotPullTheDisplayBack()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120)); // writes 121.525
        session.ApplyDetents(1, Click, At(150)); // pilot is on 121.550

        TuningOutcome echoed = session.ObserveSimValue(121_525);

        Assert.False(echoed.DisplayChanged);
        Assert.Equal(121_550, session.Displayed);
        Assert.Equal(121_525, session.Confirmed);
        Assert.Equal(PendingWriteStatus.AwaitingConfirmation, session.Status);
        Assert.Equal(121_550, session.Tick(At(270)).WriteRequest);
    }

    [Fact]
    public void AnUnconfirmedWriteIsRejectedAfterTheSettleTimeoutAndTheSimsValueShown()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120));

        // The sim moves the value itself while we wait, as VNAV does to the AP altitude.
        session.ObserveSimValue(124_850);

        Assert.Equal(PendingWriteStatus.AwaitingConfirmation, session.Tick(At(1_619)).Status);

        TuningOutcome gaveUp = session.Tick(At(1_620));

        Assert.Equal(new TuningOutcome(true, null, PendingWriteStatus.Rejected), gaveUp);
        Assert.Equal(124_850, session.Displayed);
        Assert.Equal(124_850, session.Confirmed);
    }

    [Fact]
    public void TheSettleTimeoutRestartsWithEachWrite()
    {
        TuningSession session = Tuned();

        // A write every 300 ms or so for 3 s, well past one settle timeout, never rejected.
        for (int ms = 0; ms <= 3_000; ms += 10)
        {
            if (ms % 50 == 0)
            {
                session.ApplyDetents(1, Click, At(ms));
            }

            Assert.NotEqual(PendingWriteStatus.Rejected, session.Tick(At(ms)).Status);
        }
    }

    [Fact]
    public void AfterARejectionTheNextDetentStepsFromTheSimsValue()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120));
        session.Tick(At(1_620));

        TuningOutcome turned = session.ApplyDetents(1, Click, At(2_000));

        Assert.Equal(PendingWriteStatus.AwaitingConfirmation, turned.Status);
        Assert.Equal(121_525, session.Displayed);
    }

    [Fact]
    public void TurningBackToTheStartBeforeAnyWriteSendsNothing()
    {
        TuningSession session = Tuned();

        session.ApplyDetents(1, Click, At(0));
        TuningOutcome back = session.ApplyDetents(-1, Click, At(50));

        Assert.Equal(new TuningOutcome(true, null, PendingWriteStatus.None), back);
        Assert.Equal(121_500, session.Displayed);
        Assert.Null(session.Tick(At(500)).WriteRequest);
    }

    [Fact]
    public void TurningBackToTheLastWrittenValueDoesNotWriteItAgain()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120)); // writes 121.525

        session.ApplyDetents(1, Click, At(150));
        session.ApplyDetents(-1, Click, At(200));

        Assert.Null(session.Tick(At(400)).WriteRequest);
        Assert.Equal(PendingWriteStatus.Confirmed, session.ObserveSimValue(121_525).Status);
    }

    [Fact]
    public void ADetentAtTheEndOfTheRangeChangesNothing()
    {
        TuningSession session = Tuned(136_500);
        session.ToggleCursor(); // back to MHz, which clamps

        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.None), session.ApplyDetents(1, Click, At(0)));
        Assert.Null(session.Tick(At(500)).WriteRequest);
    }

    [Fact]
    public void AnExternalChangeWhileIdleIsFollowed()
    {
        TuningSession session = Tuned();

        TuningOutcome moved = session.ObserveSimValue(127_850);

        Assert.Equal(new TuningOutcome(true, null, PendingWriteStatus.None), moved);
        Assert.Equal(127_850, session.Displayed);
        Assert.False(session.ObserveSimValue(127_850).DisplayChanged);
    }

    [Fact]
    public void ToggleCursorCyclesCoarseToFineAndRoundAgain()
    {
        TuningSession session = new(Com1Standby, TuningOptions.Default);

        Assert.Equal(Mhz, session.Cursor);
        Assert.True(session.ToggleCursor().DisplayChanged);
        Assert.Equal(Khz, session.Cursor);
        session.ToggleCursor();
        Assert.Equal(Mhz, session.Cursor);
    }

    [Fact]
    public void TheCursorDecidesWhatADetentMoves()
    {
        TuningSession session = Tuned();
        session.ToggleCursor(); // MHz

        session.ApplyDetents(1, Click, At(0));

        Assert.Equal(122_500, session.Displayed);
    }

    [Fact]
    public void ASingleCursorParameterIgnoresToggle()
    {
        TuningSession session = new(TestParameters.Com1Standby([Khz]), TuningOptions.Default);

        Assert.False(session.ToggleCursor().DisplayChanged);
        Assert.Equal(Khz, session.Cursor);
    }

    [Fact]
    public void TheSimsOwnOffGridValueIsShownAsIsAndTheFirstDetentSnapsAndSteps()
    {
        // 118.505 in 25 kHz mode: what the sim sets at the Start Flight screen (spec §5.3).
        TuningSession session = Tuned(118_505);

        Assert.Equal(118_505, session.Displayed);

        session.ApplyDetents(1, Click, At(0));

        Assert.Equal(118_525, session.Displayed);
        Assert.Equal(118_525, session.Tick(At(120)).WriteRequest);
    }

    [Fact]
    public void AccelerationScalesDetentsIntoSteps()
    {
        TuningSession session = Tuned(options: TuningOptions.Default with { Acceleration = new TimesTenWhenFast() });

        session.ApplyDetents(1, TimeSpan.FromMilliseconds(200), At(0));
        Assert.Equal(121_525, session.Displayed);

        session.ApplyDetents(1, TimeSpan.FromMilliseconds(10), At(10));
        Assert.Equal(121_775, session.Displayed);
    }

    [Fact]
    public void AnAccelerationOfZeroStepsIsANoOp()
    {
        TuningSession session = Tuned(options: TuningOptions.Default with { Acceleration = new Zero() });

        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.None), session.ApplyDetents(3, Click, At(0)));
    }

    [Fact]
    public void AParameterWithNoCursorsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TestParameters.Com1Standby([]));
    }

    [Fact]
    public void ReplacingTheGridWithNothingDialledChangesNothingVisible()
    {
        TuningSession session = Tuned();

        TuningOutcome replaced = session.ReplaceGrid(new ComChannelGrid(ChannelSpacing.EightPointThreeThree));

        Assert.Equal(new TuningOutcome(false, null, PendingWriteStatus.None), replaced);
        session.ApplyDetents(1, Click, At(0));
        Assert.Equal(121_505, session.Displayed); // steps on the new grid
    }

    [Fact]
    public void ReplacingTheGridDropsADialledValue()
    {
        TuningSession session = Tuned();
        session.ApplyDetents(1, Click, At(0));
        session.Tick(At(120)); // written, not yet confirmed

        TuningOutcome replaced = session.ReplaceGrid(new ComChannelGrid(ChannelSpacing.EightPointThreeThree));

        Assert.Equal(new TuningOutcome(true, null, PendingWriteStatus.None), replaced);
        Assert.Equal(121_500, session.Displayed);
        Assert.Equal(PendingWriteStatus.None, session.Tick(At(5_000)).Status); // no rejection later either
    }

    private sealed class TimesTenWhenFast : IEncoderAcceleration
    {
        public int Scale(int detents, TimeSpan sinceLastDetent) =>
            sinceLastDetent < TimeSpan.FromMilliseconds(30) ? detents * 10 : detents;
    }

    private sealed class Zero : IEncoderAcceleration
    {
        public int Scale(int detents, TimeSpan sinceLastDetent) => 0;
    }
}
