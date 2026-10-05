using System.Threading.Channels;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;

namespace Crowsnest.Sim;

/// <summary>
/// A sim with no sim behind it (spec §7.3, §10 Null Object), for developing and demoing the
/// whole system with neither MSFS nor hardware present.
///
/// It knows no panel by name; everything comes from the <see cref="PanelSetup"/>. It starts
/// with the modules' demo values (anything without one starts at the bottom of its grid, a
/// watch at 0). A page's swap event exchanges its two fields. Writes read back after a short
/// latency, as the 2026-09 spikes saw MSFS 2024 do, and are not policed against the grid,
/// because MSFS does not either. Anything else the sim does by itself, such as COM snapping to
/// 25 kHz when the spacing switch flips, the caller adds with <see cref="On"/>.
/// </summary>
public sealed class FakeParameterGateway : ISimParameterGateway
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Action> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<ParameterId, int> _values;
    private readonly HashSet<ParameterId> _subscribed = [];
    private readonly Channel<ParameterSnapshot> _snapshots = Channel.CreateUnbounded<ParameterSnapshot>();
    private readonly BehaviorSubject<SimConnectionState> _state = new(SimConnectionState.Connecting);
    private readonly TimeProvider _time;

    /// <param name="latency">Write to read-back. Spike 0(a) measured 10-16 ms; just after flight load, up to ~590 ms.</param>
    public FakeParameterGateway(PanelSetup setup, TimeSpan? latency = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(setup);

        Latency = latency ?? TimeSpan.FromMilliseconds(15);
        _time = time ?? TimeProvider.System;

        _values = [];
        foreach (ParameterDefinition parameter in setup.Registry.All)
        {
            _values[parameter.Id] = setup.DemoValues.TryGetValue(parameter.Id, out int value) ? value : parameter.Grid.Snap(0);
        }

        foreach (SimSubscription watch in setup.Registry.Watches)
        {
            _values[watch.Id] = setup.DemoValues.GetValueOrDefault(watch.Id);
        }

        foreach (PanelPage page in setup.Pages)
        {
            if (page.SwapEvent is { } swap)
            {
                (ParameterId a, ParameterId b) = (page.Fields[0], page.Fields[1]);
                _events[swap] = () => Swap(a, b);
            }
        }
    }

    public TimeSpan Latency { get; set; }

    /// <summary>When true, writes are accepted and forgotten, like a study-level aircraft ignoring key events (spec §14).</summary>
    public bool IgnoreWrites { get; set; }

    public IObservable<SimConnectionState> ConnectionState => _state;

    public IAsyncEnumerable<ParameterSnapshot> Snapshots => _snapshots.Reader.ReadAllAsync();

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

        _state.OnNext(SimConnectionState.Connected);
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

    /// <summary>
    /// Teaches the fake what the sim does on <paramref name="eventName"/>, replacing a page's
    /// swap if it has the same name. The action runs after <see cref="Latency"/>, and should
    /// change values through <see cref="SetFromCockpit"/>.
    /// </summary>
    public void On(string eventName, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            _events[eventName] = action;
        }
    }

    private void Apply(string eventName)
    {
        Action? action;
        lock (_gate)
        {
            // An event this fake does not model is accepted and ignored, as the sim would.
            _events.TryGetValue(eventName, out action);
        }

        action?.Invoke();
    }

    /// <summary>A change made in the cockpit rather than by us: the pilot's own knob, ATC, the aircraft.</summary>
    public void SetFromCockpit(ParameterId id, int canonicalValue) => Set(id, canonicalValue);

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
        _state.OnNext(SimConnectionState.Disconnected);
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
}
