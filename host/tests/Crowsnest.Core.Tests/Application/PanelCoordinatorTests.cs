using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;
using Crowsnest.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Crowsnest.Core.Tests.Application;

/// <summary>
/// The async shell: events in, effects out, one loop. Behaviour is covered by
/// <see cref="PanelEngineTests"/>; these check the wiring, with a fake clock driving the tick.
/// </summary>
public sealed class PanelCoordinatorTests : IAsyncDisposable
{
    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");

    private const string DeviceA = "a4cb8fdccc6c";
    private const string DeviceB = "a4cb8fdc1234";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSimGateway _sim = new();
    private readonly FakeDevice _device = new();
    private readonly FakeDevice _other = new();
    private readonly List<Exception> _failures = [];
    private readonly CancellationTokenSource _stop = new();
    private PanelCoordinator? _coordinator;
    private Task? _run;
    private Task? _simulated;

    private void Start(Func<string, IReadOnlyList<PanelPage>>? pagesFor = null)
    {
        _coordinator = new(
            PanelCatalog.Load(), _sim, DefaultInputActionMap.Instance,
            TuningOptions.Default, _time, e => { lock (_failures) { _failures.Add(e); } }, pagesFor);

        _run = _coordinator.RunAsync(_stop.Token);
        _simulated = _coordinator.RunDeviceAsync(DeviceA, _device, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (Task? task in new[] { _run, _simulated })
        {
            if (task is not null)
            {
                await task.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        _stop.Dispose();
    }

    /// <summary>Waits, in real time, for the loop to have done something.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Moves the fake clock on in tick-sized steps, letting the loop keep up.</summary>
    private async Task Advance(TimeSpan by)
    {
        for (TimeSpan moved = TimeSpan.Zero; moved < by; moved += PanelCoordinator.TickPeriod)
        {
            _time.Advance(PanelCoordinator.TickPeriod);
            await Task.Delay(5);
        }
    }

    private async Task StartTuned()
    {
        Start();
        _sim.State.OnNext(SimConnectionState.Connected);
        _sim.Push(ComStandby, 121_500);
        _sim.Push(ComActive, 118_000);
        await Eventually(() => _device.Latest?.Fields[1].Text == "118.000", "COM 1 to show");
    }

    [Fact]
    public async Task ItSubscribesEveryParameterAndRendersAtOnce()
    {
        Start();

        await Eventually(() => _device.Latest is not null, "the first frame");

        Assert.Equal(PanelCatalog.Load().Registry.Subscriptions, _sim.Subscribed);
        Assert.Equal("---.---", _device.Latest!.Fields[0].Text);
    }

    [Fact]
    public async Task SimValuesAndConnectionStateReachTheDevice()
    {
        await StartTuned();

        Assert.Equal(SimConnectionState.Connected, _device.Latest!.Sim);
        Assert.Equal("121.500", _device.Latest.Fields[0].Text);
    }

    [Fact]
    public async Task ATurnIsShownAtOnceAndWrittenWhenTheClockPassesTheDebounce()
    {
        await StartTuned();

        _device.Turn(1, _time.GetUtcNow());
        await Eventually(() => _device.Latest?.Fields[0].Text == "122.500", "the turn to show");
        Assert.Empty(_sim.Commands);

        await Advance(TimeSpan.FromMilliseconds(200));

        await Eventually(() => !_sim.Commands.IsEmpty, "the write");
        Assert.Equal([new SimCommand.Write(ComStandby, 122_500)], _sim.Commands);
    }

    [Fact]
    public async Task ATapSendsTheSwapEvent()
    {
        await StartTuned();

        _device.Tap(_time.GetUtcNow());

        await Eventually(() => !_sim.Commands.IsEmpty, "the swap");
        Assert.Equal([new SimCommand.Invoke("COM_STBY_RADIO_SWAP")], _sim.Commands);
    }

    [Fact]
    public async Task AFailedWriteIsReportedAndTheLoopCarriesOn()
    {
        await StartTuned();
        _sim.FailWritesWith = new InvalidOperationException("sim went away");

        _device.Turn(1, _time.GetUtcNow());
        await Advance(TimeSpan.FromMilliseconds(200));
        await Eventually(() => { lock (_failures) { return _failures.Count > 0; } }, "the failure to be reported");

        _sim.Push(ComActive, 124_850);
        await Eventually(() => _device.Latest?.Fields[1].Text == "124.850", "the loop to keep going");
    }

    [Fact]
    public async Task ADeviceThatReconnectsIsSentAFreshFrame()
    {
        await StartTuned();
        int before = _device.Frames.Count;

        _device.State.OnNext(DeviceConnectionState.Disconnected);
        _device.State.OnNext(DeviceConnectionState.Connected);

        await Eventually(() => _device.Frames.Count > before, "a frame after reconnecting");
        Assert.Equal("121.500", _device.Latest!.Fields[0].Text);
    }

    [Fact]
    public async Task AFailedInputStreamEndsOnlyThatDevice()
    {
        await StartTuned();
        Task other = _coordinator!.RunDeviceAsync(DeviceB, _other, _stop.Token);
        await Eventually(() => _other.Latest is not null, "the second device's first frame");

        _device.Fail(new IOException("cable pulled"));

        IOException e = await Assert.ThrowsAsync<IOException>(() => _simulated!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("cable pulled", e.Message);
        _simulated = null;

        _sim.Push(ComActive, 124_850);
        await Eventually(() => _other.Latest?.Fields[1].Text == "124.850", "the other device to carry on");
        Assert.False(_run!.IsCompleted);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task AFaultedDeviceEndsOnlyThatDeviceAndGetsNoMoreFrames()
    {
        await StartTuned();

        _device.State.OnNext(DeviceConnectionState.Faulted);

        await Assert.ThrowsAsync<IOException>(() => _simulated!.WaitAsync(TimeSpan.FromSeconds(5)));
        _simulated = null;
        int frames = _device.Frames.Count;

        _sim.Push(ComActive, 124_850);
        _sim.State.OnNext(SimConnectionState.Disconnected);
        await Task.Delay(100);

        Assert.Equal(frames, _device.Frames.Count);
        Assert.False(_run!.IsCompleted);
    }

    [Fact]
    public async Task TwoDevicesShareTuningButNavigateApart()
    {
        await StartTuned();
        Task other = _coordinator!.RunDeviceAsync(DeviceB, _other, _stop.Token);
        await Eventually(() => _other.Latest?.Fields[0].Text == "121.500", "the second device to show COM 1");

        _device.Turn(1, _time.GetUtcNow());
        await Eventually(() => _other.Latest?.Fields[0].Text == "122.500", "the turn to show on the other device");

        _other.Swipe(SwipeDir.Left, _time.GetUtcNow());
        await Eventually(() => _other.Latest?.Page.Id == "com2", "the other device to change page");
        Assert.Equal("com1", _device.Latest!.Page.Id);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task DevicesShowTheirAssignedPagesAndFollowNewSettings()
    {
        PanelSetup setup = PanelCatalog.Load();
        Dictionary<string, string?> assigned = new() { [DeviceA] = null, [DeviceB] = "nav" };
        Start(id =>
        {
            lock (assigned)
            {
                return setup.PagesFor(assigned[id]);
            }
        });
        Task other = _coordinator!.RunDeviceAsync(DeviceB, _other, _stop.Token);
        await Eventually(() => _device.Latest?.Page.Id == PanelEngine.UnassignedPageId, "the unassigned screen");
        await Eventually(() => _other.Latest?.Page.Id == "nav1", "the second device to show NAV 1");
        int otherFrames = _other.Frames.Count;

        lock (assigned)
        {
            assigned[DeviceA] = "com";
        }

        _coordinator.Reassign();

        await Eventually(() => _device.Latest?.Page.Id == "com1", "the first device to show COM 1");
        Assert.Equal(otherFrames, _other.Frames.Count); // its pages did not change, so no redraw
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task ASecondDeviceWithTheSameIdIsRefused()
    {
        await StartTuned();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _coordinator!.RunDeviceAsync(DeviceA, _other, _stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        // The real one is untouched.
        _sim.Push(ComActive, 124_850);
        await Eventually(() => _device.Latest?.Fields[1].Text == "124.850", "the first device to carry on");
        Assert.Empty(_other.Frames);
    }

    [Fact]
    public async Task ADeviceReturnsWhenTheCoordinatorStops()
    {
        using CancellationTokenSource coordinatorOnly = new();
        _coordinator = new(PanelCatalog.Load(), _sim, DefaultInputActionMap.Instance, TuningOptions.Default, _time);
        _run = _coordinator.RunAsync(coordinatorOnly.Token);
        _simulated = _coordinator.RunDeviceAsync(DeviceA, _device, _stop.Token);
        await Eventually(() => _device.Latest is not null, "the first frame");

        await coordinatorOnly.CancelAsync();

        await _simulated.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_simulated.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellingStopsTheRunCleanly()
    {
        await StartTuned();

        await _stop.CancelAsync();

        await _run!.WaitAsync(TimeSpan.FromSeconds(5));
        await _simulated!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_run.IsCompletedSuccessfully);
        Assert.True(_simulated.IsCompletedSuccessfully);
    }
}
