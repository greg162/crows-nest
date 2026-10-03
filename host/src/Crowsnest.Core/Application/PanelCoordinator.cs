using System.Threading.Channels;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain.Tuning;

namespace Crowsnest.Core.Application;

/// <summary>
/// Connects the sim to the devices (spec §5.7, §6.2). Inputs from every device, sim values,
/// connection changes and a 20 ms tick all go onto one queue and are handled one at a time by a
/// <see cref="PanelEngine"/>, so there are no locks anywhere in Core. The coordinator only moves
/// events in and carries the engine's effects out: sim writes and swaps first, then the frames.
///
/// One coordinator serves every device, so devices share tuning sessions (§6.2: two devices on
/// COM 1 must not fight over a pending write). <see cref="RunAsync"/> runs for the life of the
/// bridge; devices come and go through <see cref="RunDeviceAsync"/>, and one device's link failing
/// never stops the others.
///
/// It does not own the sim or the devices; whoever built them disposes them.
/// </summary>
public sealed class PanelCoordinator
{
    public static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(20);

    private readonly ISimParameterGateway _sim;
    private readonly ParameterRegistry _registry;
    private readonly PanelEngine _engine;
    private readonly TimeProvider _time;
    private readonly Action<Exception>? _onEffectFailed;
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Touched only by the loop.
    private readonly Dictionary<string, IDevice> _devices = new(StringComparer.Ordinal);

    /// <param name="setup">The registry, the pages every device shows, and panel runtime logic such as COM following the spacing mode (§5.8).</param>
    /// <param name="onEffectFailed">
    /// Called when a sim write, swap or render throws. The loop carries on: a lost write is
    /// caught by the settle timeout, a lost frame by the next one. Core has no logger (spec §4:
    /// BCL only), so the host passes one in here.
    /// </param>
    public PanelCoordinator(PanelSetup setup, ISimParameterGateway sim, IInputActionMap actions, TuningOptions options, TimeProvider time, Action<Exception>? onEffectFailed = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(time);

        _sim = sim;
        _registry = setup.Registry;
        _engine = new PanelEngine(setup.Registry, setup.Pages, actions, options, setup.Behaviours);
        _time = time;
        _onEffectFailed = onEffectFailed;
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or faults if the sim stream does. Call once.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using IDisposable simState = _sim.ConnectionState.Subscribe(new Forward<SimConnectionState>(_events, s => new SimStateChanged(s)));

        Task[] producers = [];
        try
        {
            await _sim.SubscribeAsync(_registry.Subscriptions, ct).ConfigureAwait(false);

            producers =
            [
                Produce(PumpSnapshots(stop.Token)),
                Produce(PumpTicks(stop.Token)),
            ];

            await foreach (BridgeEvent e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                DateTimeOffset now = _time.GetUtcNow();
                PanelEffects effects = e switch
                {
                    DeviceJoined joined => Join(joined),
                    DeviceLeft left => Leave(left),
                    InputReceived input => _engine.OnInput(input.DeviceId, input.Input, now),
                    DeviceRestarted restarted when _devices.ContainsKey(restarted.DeviceId) => _engine.OnDeviceRestarted(restarted.DeviceId),
                    SnapshotReceived snapshot => _engine.OnSnapshot(snapshot.Snapshot),
                    SimStateChanged state => _engine.OnSimConnection(state.State),
                    Tick => _engine.OnTick(now),
                    _ => PanelEffects.None,
                };

                await ApplyAsync(effects, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Asked to stop.
        }
        finally
        {
            _stopped.TrySetResult();
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(producers.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves one connected device until <paramref name="ct"/> is cancelled (returns), the
    /// coordinator stops (returns), or the device's link fails (throws). The caller then disposes
    /// the device; by then the loop has stopped rendering to it.
    /// </summary>
    /// <param name="deviceId">The device's hardware id (spec §6.2): never a port name.</param>
    public async Task RunDeviceAsync(string deviceId, IDevice device, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(device);

        TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new DeviceJoined(deviceId, device, joined)))
        {
            return;
        }

        if (await Task.WhenAny(joined.Task, _stopped.Task).ConfigureAwait(false) != joined.Task)
        {
            return;
        }

        await joined.Task.ConfigureAwait(false); // a device already serving this id throws here

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        DeviceWatch watch = new(_events, deviceId);
        Task pump = PumpInputs(deviceId, device, stop.Token);
        try
        {
            using IDisposable state = device.ConnectionState.Subscribe(watch);
            Task ended = await Task.WhenAny(pump, watch.Faulted, _stopped.Task, Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false);

            if (ended == watch.Faulted)
            {
                // The link is dead (cable pulled, heartbeat lost) and a faulted connection never
                // recovers by itself. The owner finds the device again.
                throw new IOException($"Device {deviceId}'s link failed.");
            }

            if (ended == pump)
            {
                await pump.ConfigureAwait(false); // the input stream's own error, if it had one
                if (!ct.IsCancellationRequested)
                {
                    throw new IOException($"Device {deviceId} stopped sending input.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Asked to stop.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);

            // Wait for the loop to forget the device, so nothing renders to it once disposed.
            TaskCompletionSource left = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_events.Writer.TryWrite(new DeviceLeft(deviceId, device, left)))
            {
                await Task.WhenAny(left.Task, _stopped.Task).ConfigureAwait(false);
            }
        }
    }

    private PanelEffects Join(DeviceJoined joined)
    {
        if (!_devices.TryAdd(joined.DeviceId, joined.Device))
        {
            joined.Done.TrySetException(new InvalidOperationException($"Device {joined.DeviceId} is already connected."));
            return PanelEffects.None;
        }

        joined.Done.TrySetResult();
        return _engine.Join(joined.DeviceId);
    }

    private PanelEffects Leave(DeviceLeft left)
    {
        // A refused duplicate leaves too; it must not take the real device with it.
        if (_devices.TryGetValue(left.DeviceId, out IDevice? device) && device == left.Device)
        {
            _devices.Remove(left.DeviceId);
            _engine.Leave(left.DeviceId);
        }

        left.Done.TrySetResult();
        return PanelEffects.None;
    }

    private async Task ApplyAsync(PanelEffects effects, CancellationToken ct)
    {
        foreach (SimCommand command in effects.Sim)
        {
            await Guard(command switch
            {
                SimCommand.Write write => _sim.WriteAsync(write.Id, write.Value, ct),
                SimCommand.Invoke invoke => _sim.InvokeAsync(invoke.EventName, 0, ct),
                _ => Task.CompletedTask,
            }).ConfigureAwait(false);
        }

        if (effects.Frames.Count > 0)
        {
            await Task.WhenAll(effects.Frames
                .Where(f => _devices.ContainsKey(f.DeviceId))
                .Select(f => Guard(_devices[f.DeviceId].RenderAsync(f.Frame, ct)))).ConfigureAwait(false);
        }
    }

    private async Task Guard(Task effect)
    {
        try
        {
            await effect.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _onEffectFailed?.Invoke(e);
        }
    }

    private async Task PumpInputs(string deviceId, IDevice device, CancellationToken ct)
    {
        await foreach (DeviceInputEvent input in device.Inputs.WithCancellation(ct).ConfigureAwait(false))
        {
            await _events.Writer.WriteAsync(new InputReceived(deviceId, input), ct).ConfigureAwait(false);
        }
    }

    private async Task PumpSnapshots(CancellationToken ct)
    {
        await foreach (ParameterSnapshot snapshot in _sim.Snapshots.WithCancellation(ct).ConfigureAwait(false))
        {
            await _events.Writer.WriteAsync(new SnapshotReceived(snapshot), ct).ConfigureAwait(false);
        }
    }

    private async Task PumpTicks(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TickPeriod, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await _events.Writer.WriteAsync(Tick.Instance, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A sim producer that fails takes the loop down with it rather than leaving it deaf.</summary>
    private async Task Produce(Task producer)
    {
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception e)
        {
            _events.Writer.TryComplete(e);
        }
    }

    private abstract record BridgeEvent;

    private sealed record DeviceJoined(string DeviceId, IDevice Device, TaskCompletionSource Done) : BridgeEvent;

    private sealed record DeviceLeft(string DeviceId, IDevice Device, TaskCompletionSource Done) : BridgeEvent;

    private sealed record DeviceRestarted(string DeviceId) : BridgeEvent;

    private sealed record InputReceived(string DeviceId, DeviceInputEvent Input) : BridgeEvent;

    private sealed record SnapshotReceived(ParameterSnapshot Snapshot) : BridgeEvent;

    private sealed record SimStateChanged(SimConnectionState State) : BridgeEvent;

    private sealed record Tick : BridgeEvent
    {
        public static Tick Instance { get; } = new();
    }

    private sealed class Forward<T>(Channel<BridgeEvent> events, Func<T, BridgeEvent> wrap) : IObserver<T>
    {
        public void OnNext(T value) => events.Writer.TryWrite(wrap(value));

        public void OnError(Exception error) => events.Writer.TryComplete(error);

        public void OnCompleted()
        {
        }
    }

    /// <summary>
    /// Watches one device's link. Connected again after anything else means the device restarted
    /// under an open port and needs its screen back. The first state seen is the one it joined
    /// in, which the join has already drawn.
    /// </summary>
    private sealed class DeviceWatch(Channel<BridgeEvent> events, string deviceId) : IObserver<DeviceConnectionState>
    {
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DeviceConnectionState? _last;

        public Task Faulted => _faulted.Task;

        public void OnNext(DeviceConnectionState value)
        {
            if (value == DeviceConnectionState.Faulted)
            {
                _faulted.TrySetResult();
            }
            else if (value == DeviceConnectionState.Connected && _last is { } last && last != DeviceConnectionState.Connected)
            {
                events.Writer.TryWrite(new DeviceRestarted(deviceId));
            }

            _last = value;
        }

        public void OnError(Exception error) => _faulted.TrySetResult();

        public void OnCompleted()
        {
        }
    }
}
