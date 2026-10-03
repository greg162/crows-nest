using Crowsnest.Core.Application;
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

    private const string PanelA = "a4cb8fdccc6c";
    private const string PanelB = "a4cb8fdc1234";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSimGateway _sim = new();
    private readonly FakePanelDevice _device = new();
    private readonly FakePanelDevice _other = new();
    private readonly List<Exception> _failures = [];
    private readonly CancellationTokenSource _stop = new();
    private PanelCoordinator? _coordinator;
    private Task? _run;
    private Task? _panel;

    private void Start()
    {
        _coordinator = new(
            DefaultParameters.Load(), _sim, DefaultInputActionMap.Instance,
            TuningOptions.Default, _time, e => { lock (_failures) { _failures.Add(e); } });

        _run = _coordinator.RunAsync(_stop.Token);
        _panel = _coordinator.RunPanelAsync(PanelA, _device, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (Task? task in new[] { _run, _panel })
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

        Assert.Equal(DefaultParameters.Load().Registry.Subscriptions, _sim.Subscribed);
        Assert.Equal("---.---", _device.Latest!.Fields[0].Text);
    }

    [Fact]
    public async Task SimValuesAndConnectionStateReachThePanel()
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
    public async Task AFailedInputStreamEndsOnlyThatPanel()
    {
        await StartTuned();
        Task other = _coordinator!.RunPanelAsync(PanelB, _other, _stop.Token);
        await Eventually(() => _other.Latest is not null, "the second panel's first frame");

        _device.Fail(new IOException("cable pulled"));

        IOException e = await Assert.ThrowsAsync<IOException>(() => _panel!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("cable pulled", e.Message);
        _panel = null;

        _sim.Push(ComActive, 124_850);
        await Eventually(() => _other.Latest?.Fields[1].Text == "124.850", "the other panel to carry on");
        Assert.False(_run!.IsCompleted);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task AFaultedDeviceEndsOnlyThatPanelAndGetsNoMoreFrames()
    {
        await StartTuned();

        _device.State.OnNext(DeviceConnectionState.Faulted);

        await Assert.ThrowsAsync<IOException>(() => _panel!.WaitAsync(TimeSpan.FromSeconds(5)));
        _panel = null;
        int frames = _device.Frames.Count;

        _sim.Push(ComActive, 124_850);
        _sim.State.OnNext(SimConnectionState.Disconnected);
        await Task.Delay(100);

        Assert.Equal(frames, _device.Frames.Count);
        Assert.False(_run!.IsCompleted);
    }

    [Fact]
    public async Task TwoPanelsShareTuningButNavigateApart()
    {
        await StartTuned();
        Task other = _coordinator!.RunPanelAsync(PanelB, _other, _stop.Token);
        await Eventually(() => _other.Latest?.Fields[0].Text == "121.500", "the second panel to show COM 1");

        _device.Turn(1, _time.GetUtcNow());
        await Eventually(() => _other.Latest?.Fields[0].Text == "122.500", "the turn to show on the other panel");

        _other.Swipe(SwipeDir.Left, _time.GetUtcNow());
        await Eventually(() => _other.Latest?.Page.Id == "com2", "the other panel to change page");
        Assert.Equal("com1", _device.Latest!.Page.Id);
        Assert.False(other.IsCompleted);
    }

    [Fact]
    public async Task ASecondPanelWithTheSameIdIsRefused()
    {
        await StartTuned();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _coordinator!.RunPanelAsync(PanelA, _other, _stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        // The real one is untouched.
        _sim.Push(ComActive, 124_850);
        await Eventually(() => _device.Latest?.Fields[1].Text == "124.850", "the first panel to carry on");
        Assert.Empty(_other.Frames);
    }

    [Fact]
    public async Task APanelReturnsWhenTheCoordinatorStops()
    {
        using CancellationTokenSource coordinatorOnly = new();
        _coordinator = new(DefaultParameters.Load(), _sim, DefaultInputActionMap.Instance, TuningOptions.Default, _time);
        _run = _coordinator.RunAsync(coordinatorOnly.Token);
        _panel = _coordinator.RunPanelAsync(PanelA, _device, _stop.Token);
        await Eventually(() => _device.Latest is not null, "the first frame");

        await coordinatorOnly.CancelAsync();

        await _panel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_panel.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellingStopsTheRunCleanly()
    {
        await StartTuned();

        await _stop.CancelAsync();

        await _run!.WaitAsync(TimeSpan.FromSeconds(5));
        await _panel!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_run.IsCompletedSuccessfully);
        Assert.True(_panel.IsCompletedSuccessfully);
    }
}
