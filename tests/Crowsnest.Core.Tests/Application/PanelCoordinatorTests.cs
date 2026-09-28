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

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSimGateway _sim = new();
    private readonly FakePanelDevice _device = new();
    private readonly List<Exception> _failures = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    private void Start()
    {
        PanelCoordinator coordinator = new(
            DefaultParameters.Load(), _sim, _device, DefaultInputActionMap.Instance,
            TuningOptions.Default, _time, e => { lock (_failures) { _failures.Add(e); } });

        _run = coordinator.RunAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
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
        _sim.State.Publish(SimConnectionState.Connected);
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

        _device.State.Publish(DeviceConnectionState.Disconnected);
        _device.State.Publish(DeviceConnectionState.Connected);

        await Eventually(() => _device.Frames.Count > before, "a frame after reconnecting");
        Assert.Equal("121.500", _device.Latest!.Fields[0].Text);
    }

    [Fact]
    public async Task AFailedInputStreamFaultsTheRun()
    {
        await StartTuned();

        _device.Fail(new IOException("cable pulled"));

        IOException e = await Assert.ThrowsAsync<IOException>(() => _run!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("cable pulled", e.Message);
        _run = null;
    }

    [Fact]
    public async Task CancellingStopsTheRunCleanly()
    {
        await StartTuned();

        await _stop.CancelAsync();

        await _run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_run.IsCompletedSuccessfully);
    }
}
