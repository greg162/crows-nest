using System.Threading.Channels;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain.Tuning;

namespace Crowsnest.Core.Application;

/// <summary>
/// Connects the sim to the panel (spec §5.7). Device inputs, sim values, connection changes and
/// a 20 ms tick all go onto one queue and are handled one at a time by a <see cref="PanelEngine"/>,
/// so there are no locks anywhere in Core. The coordinator only moves events in and carries
/// the engine's effects out: sim writes and swaps first, then the frame.
///
/// It does not own the sim or the device; whoever built them disposes them.
/// </summary>
public sealed class PanelCoordinator
{
    public static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(20);

    private readonly ISimParameterGateway _sim;
    private readonly IPanelDevice _device;
    private readonly ParameterRegistry _registry;
    private readonly PanelEngine _engine;
    private readonly TimeProvider _time;
    private readonly Action<Exception>? _onEffectFailed;

    /// <param name="behaviours">Panel runtime logic, such as COM following the spacing mode (§5.8).</param>
    /// <param name="onEffectFailed">
    /// Called when a sim write, swap or render throws. The loop carries on: a lost write is
    /// caught by the settle timeout, a lost frame by the next one. Core has no logger (spec §4:
    /// BCL only), so the host passes one in here.
    /// </param>
    public PanelCoordinator(PanelSetup setup, ISimParameterGateway sim, IPanelDevice device, IInputActionMap actions, TuningOptions options, TimeProvider time, Action<Exception>? onEffectFailed = null)
        : this(sim, device, (setup ?? throw new ArgumentNullException(nameof(setup))).Registry, setup.Pages, actions, options, time, setup.Behaviours, onEffectFailed)
    {
    }

    public PanelCoordinator(
        ISimParameterGateway sim,
        IPanelDevice device,
        ParameterRegistry registry,
        IReadOnlyList<PanelPage> pages,
        IInputActionMap actions,
        TuningOptions options,
        TimeProvider time,
        IReadOnlyList<IPanelBehaviour>? behaviours = null,
        Action<Exception>? onEffectFailed = null)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(time);

        _sim = sim;
        _device = device;
        _registry = registry;
        _engine = new PanelEngine(registry, pages, actions, options, behaviours);
        _time = time;
        _onEffectFailed = onEffectFailed;
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or faults if the sim or device stream does or the device reports a fault.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Channel<BridgeEvent> events = Channel.CreateUnbounded<BridgeEvent>(new UnboundedChannelOptions { SingleReader = true });
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using IDisposable simState = _sim.ConnectionState.Subscribe(new Forward<SimConnectionState>(events, s => new SimStateChanged(s)));
        using IDisposable deviceState = _device.ConnectionState.Subscribe(new Forward<DeviceConnectionState>(events, s => new DeviceStateChanged(s)));

        await _sim.SubscribeAsync(_registry.Subscriptions, ct).ConfigureAwait(false);

        Task[] producers =
        [
            Produce(events, PumpInputs(events.Writer, stop.Token)),
            Produce(events, PumpSnapshots(events.Writer, stop.Token)),
            Produce(events, PumpTicks(events.Writer, stop.Token)),
        ];

        try
        {
            await RenderAsync(_engine.Render(), ct).ConfigureAwait(false);

            await foreach (BridgeEvent e in events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (e is DeviceStateChanged { State: DeviceConnectionState.Faulted })
                {
                    // The link is dead (cable pulled, heartbeat lost) and a faulted connection
                    // never recovers by itself. End the run so the owner can find the panel again.
                    throw new IOException("The panel's link failed.");
                }

                DateTimeOffset now = _time.GetUtcNow();
                PanelEffects effects = e switch
                {
                    InputReceived input => _engine.OnInput(input.Input, now),
                    SnapshotReceived snapshot => _engine.OnSnapshot(snapshot.Snapshot),
                    SimStateChanged state => _engine.OnSimConnection(state.State),
                    DeviceStateChanged { State: DeviceConnectionState.Connected } => new PanelEffects([], _engine.Render()),
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
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(producers.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        }
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

        if (effects.Frame is { } frame)
        {
            await RenderAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private Task RenderAsync(DisplayFrame frame, CancellationToken ct) => Guard(_device.RenderAsync(frame, ct));

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

    private async Task PumpInputs(ChannelWriter<BridgeEvent> writer, CancellationToken ct)
    {
        await foreach (DeviceInputEvent input in _device.Inputs.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(new InputReceived(input), ct).ConfigureAwait(false);
        }
    }

    private async Task PumpSnapshots(ChannelWriter<BridgeEvent> writer, CancellationToken ct)
    {
        await foreach (ParameterSnapshot snapshot in _sim.Snapshots.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(new SnapshotReceived(snapshot), ct).ConfigureAwait(false);
        }
    }

    private async Task PumpTicks(ChannelWriter<BridgeEvent> writer, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TickPeriod, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(Tick.Instance, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A producer that fails takes the loop down with it rather than leaving it deaf.</summary>
    private static async Task Produce(Channel<BridgeEvent> events, Task producer)
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
            events.Writer.TryComplete(e);
        }
    }

    private abstract record BridgeEvent;

    private sealed record InputReceived(DeviceInputEvent Input) : BridgeEvent;

    private sealed record SnapshotReceived(ParameterSnapshot Snapshot) : BridgeEvent;

    private sealed record SimStateChanged(SimConnectionState State) : BridgeEvent;

    private sealed record DeviceStateChanged(DeviceConnectionState State) : BridgeEvent;

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
}
