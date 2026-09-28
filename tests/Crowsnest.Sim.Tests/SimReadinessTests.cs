namespace Crowsnest.Sim.Tests;

/// <summary>The spec §5.3 readiness rule, against what the aircraft matrix saw.</summary>
public class SimReadinessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    private static SimReadiness Fresh()
    {
        SimReadiness readiness = new(SimReadiness.DefaultQuiet);
        readiness.Reset(T0);
        return readiness;
    }

    [Fact]
    public void NotReadyUntilACameraIsReported()
    {
        SimReadiness readiness = Fresh();

        Assert.False(readiness.Check(At(60)));
        Assert.False(readiness.IsReady);
    }

    [Theory]
    [InlineData(32)] // menus
    [InlineData(35)] // loading
    [InlineData(12)] // aircraft selection
    [InlineData(30)] // fly-in
    [InlineData(16)] // the Start Flight screen: the sim sets 118.505 here
    [InlineData(0)] // transitions
    public void NonFlyingCamerasNeverMakeItReady(int camera)
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(camera, At(0));

        Assert.False(readiness.Check(At(60)));
    }

    [Theory]
    [InlineData(2)] // cockpit
    [InlineData(3)] // external
    [InlineData(4)] // showcase
    public void AFlyingCameraAndFiveQuietSecondsMakeItReady(int camera)
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(camera, At(10));

        Assert.False(readiness.Check(At(14.9)));
        Assert.True(readiness.Check(At(15)));
        Assert.True(readiness.IsReady);
        Assert.False(readiness.Check(At(16))); // only reported once
    }

    [Fact]
    public void TheTriStarsLateWriteRestartsTheQuietPeriod()
    {
        // Camera 2 at 112.9 s, the TriStar's last write 1.4 s later.
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(112.9));
        readiness.OnUnsolicitedChange(At(114.3));

        Assert.False(readiness.Check(At(118)));
        Assert.True(readiness.Check(At(119.3)));
    }

    [Fact]
    public void ChangesBeforeTheHandoverDoNotDelayItFurther()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(16, At(0));
        readiness.OnUnsolicitedChange(At(5));
        readiness.OnCamera(2, At(6));

        Assert.True(readiness.Check(At(11)));
    }

    [Fact]
    public void ChangesAfterReadinessAreIgnored()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(0));
        readiness.Check(At(5));

        readiness.OnUnsolicitedChange(At(6)); // the pilot's own cockpit knob

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void GoingBackToTheMenuLosesReadinessUntilTheNextFlightSettles()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(0));
        readiness.Check(At(5));

        Assert.True(readiness.OnCamera(32, At(100)));
        Assert.False(readiness.IsReady);
        Assert.False(readiness.OnCamera(35, At(101))); // already not ready

        readiness.OnCamera(2, At(200));
        Assert.False(readiness.Check(At(204)));
        Assert.True(readiness.Check(At(205)));
    }

    [Fact]
    public void SwitchingBetweenFlyingViewsKeepsReadiness()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(0));
        readiness.Check(At(5));

        Assert.False(readiness.OnCamera(3, At(10)));
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void ThePauseMenuKeepsReadinessSoReturningToTheCockpitIsInstant()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(0));
        readiness.Check(At(5));

        Assert.False(readiness.OnCamera(29, At(60))); // Esc
        Assert.True(readiness.IsReady);
        Assert.False(readiness.OnCamera(2, At(70))); // back to the cockpit
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void ThePauseMenuDoesNotCountAsFlyingBeforeReadiness()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(29, At(0));

        Assert.False(readiness.Check(At(60)));
    }

    [Fact]
    public void RestartingFromThePauseMenuStillLosesReadiness()
    {
        SimReadiness readiness = Fresh();
        readiness.OnCamera(2, At(0));
        readiness.Check(At(5));
        readiness.OnCamera(29, At(60));

        Assert.True(readiness.OnCamera(35, At(62))); // loading
        Assert.False(readiness.IsReady);
    }
}
