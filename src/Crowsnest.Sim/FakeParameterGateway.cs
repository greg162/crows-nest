using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Sim;

/// <summary>
/// A sim with no sim behind it (spec §7.3, §10 Null Object), for developing and demoing the
/// whole system with neither MSFS nor hardware present.
///
/// It behaves the way the 2026-09 spikes saw MSFS 2024 behave: writes read back after a short
/// latency; <c>COM_STBY_RADIO_SWAP</c> and <c>COM2_RADIO_SWAP</c> exchange active and standby;
/// <c>COM_1_SPACING_MODE_SWITCH</c> toggles <c>com1.spacing</c>, and switching to 25 kHz snaps
/// both COM 1 values onto the 25 kHz grid (spec §5.3). It does not police spacing on writes,
/// because MSFS does not either.
/// </summary>
public sealed class FakeParameterGateway : ISimParameterGateway
{
    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");
    private static readonly ParameterId ComSpacing = new("com1.spacing");
    private static readonly ParameterId Com2Standby = new("com2.standby");
    private static readonly ParameterId Com2Active = new("com2.active");
    private static readonly ParameterId NavStandby = new("nav1.standby");
    private static readonly ParameterId NavActive = new("nav1.active");

    private readonly Lock _gate = new();
    private readonly Dictionary<ParameterId, int> _values;
    private readonly HashSet<ParameterId> _subscribed = [];
    private readonly Channel<ParameterSnapshot> _snapshots = Channel.CreateUnbounded<ParameterSnapshot>();
    private readonly StateSubject<SimConnectionState> _state = new(SimConnectionState.Connecting);
    private readonly TimeProvider _time;

    /// <param name="latency">Write to read-back. Spike 0(a) measured 10-16 ms; just after flight load, up to ~590 ms.</param>
    public FakeParameterGateway(TimeSpan? latency = null, TimeProvider? time = null)
    {
        Latency = latency ?? TimeSpan.FromMilliseconds(15);
        _time = time ?? TimeProvider.System;

        // A C172 on the ground at a UK airport, after the handover (spec §5.3).
        _values = new Dictionary<ParameterId, int>
        {
            [ComActive] = 127_850,
            [ComStandby] = 124_850,
            [ComSpacing] = 0,
            [Com2Active] = 121_500,
            [Com2Standby] = 119_875,
            [new ParameterId("com2.spacing")] = 0,
            [NavActive] = 113_900,
            [NavStandby] = 110_300,
        };
    }

    public TimeSpan Latency { get; set; }

    /// <summary>When true, writes are accepted and forgotten, like a study-level aircraft ignoring key events (spec §14).</summary>
    public bool IgnoreWrites { get; set; }

    public IObservable<SimConnectionState> ConnectionState => _state;

    public IAsyncEnumerable<ParameterSnapshot> Snapshots => Read();

    public Task SubscribeAsync(IReadOnlyList<SimSubscription> subscriptions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        lock (_gate)
        {
            foreach (SimSubscription subscription in subscriptions)
            {
                _subscribed.Add(subscription.Id);
                if (_values.TryGetValue(subscription.Id, out int value))
                {
                    _snapshots.Writer.TryWrite(new ParameterSnapshot(subscription.Id, value));
                }
            }
        }

        _state.Publish(SimConnectionState.Connected);
        return Task.CompletedTask;
    }

    /// <summary>Returns once "sent", like SimConnect's transmit; the value reads back after <see cref="Latency"/>.</summary>
    public Task WriteAsync(ParameterId id, int canonicalValue, CancellationToken ct)
    {
        if (!IgnoreWrites)
        {
            Later(() => Set(id, canonicalValue));
        }

        return Task.CompletedTask;
    }

    public Task InvokeAsync(string eventName, uint payload, CancellationToken ct)
    {
        Later(() => Apply(eventName));
        return Task.CompletedTask;
    }

    private void Apply(string eventName)
    {
        switch (eventName)
        {
            case "COM_STBY_RADIO_SWAP" or "COM1_RADIO_SWAP":
                Swap(ComStandby, ComActive);
                break;

            case "COM2_RADIO_SWAP":
                Swap(Com2Standby, Com2Active);
                break;

            case "NAV1_RADIO_SWAP":
                Swap(NavStandby, NavActive);
                break;

            case "COM_1_SPACING_MODE_SWITCH":
                ToggleSpacing();
                break;

            default:
                // An event this fake does not model: accepted and ignored, as the sim would.
                break;
        }
    }

    /// <summary>A change made in the cockpit rather than by us: the pilot's own knob, ATC, the aircraft.</summary>
    public void SetFromCockpit(ParameterId id, int canonicalValue) => Set(id, canonicalValue);

    /// <summary>The cockpit spacing switch.</summary>
    public void ToggleSpacing()
    {
        lock (_gate)
        {
            int mode = _values[ComSpacing] == 0 ? 1 : 0;
            SetLocked(ComSpacing, mode);

            if (mode == 0)
            {
                // Verified in all three aircraft: 118.505 → 118.500, 119.005 → 119.000.
                ComChannelGrid grid = new(ChannelSpacing.TwentyFiveKhz);
                SetLocked(ComActive, grid.Snap(_values[ComActive]));
                SetLocked(ComStandby, grid.Snap(_values[ComStandby]));
            }
        }
    }

    // Fire and forget on purpose: the sim applies the change on its own time, and the caller
    // learns of it from the snapshot, exactly as with SimConnect.
    private void Later(Action apply) =>
        _ = Task.Delay(Latency, _time).ContinueWith(_ => apply(), TaskScheduler.Default);

    public int Value(ParameterId id)
    {
        lock (_gate)
        {
            return _values[id];
        }
    }

    public ValueTask DisposeAsync()
    {
        _snapshots.Writer.TryComplete();
        _state.Publish(SimConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    private void Swap(ParameterId a, ParameterId b)
    {
        lock (_gate)
        {
            (int first, int second) = (_values[a], _values[b]);
            SetLocked(a, second);
            SetLocked(b, first);
        }
    }

    private void Set(ParameterId id, int value)
    {
        lock (_gate)
        {
            SetLocked(id, value);
        }
    }

    // Like SimConnect with CHANGED: a snapshot only when the value actually moves.
    private void SetLocked(ParameterId id, int value)
    {
        if (_values.TryGetValue(id, out int old) && old == value)
        {
            return;
        }

        _values[id] = value;
        if (_subscribed.Contains(id))
        {
            _snapshots.Writer.TryWrite(new ParameterSnapshot(id, value));
        }
    }

    private async IAsyncEnumerable<ParameterSnapshot> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (ParameterSnapshot snapshot in _snapshots.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }
}
