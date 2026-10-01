using System.Collections.Concurrent;
using System.Threading.Channels;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.SimConnect;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Sim;

/// <summary>
/// The real sim (spec §7.3), driven entirely by the registry: every subscription becomes a
/// watched SimVar, every write its parameter's key event.
///
/// <see cref="RunAsync"/> owns the connection: it connects when the sim appears, retries with
/// a jittered backoff from 1 s to 30 s while it does not (spec §7.2), and starts again after the
/// sim quits. Within a session it applies the readiness rule (spec §5.3): values are held back
/// and writes dropped until <c>CAMERA STATE</c> is a flying view and nothing has changed on its
/// own for <see cref="SimReadiness.Quiet"/>. The connection state reads <c>Connecting</c> until then.
///
/// Values are watched every visual frame with SimConnect's <c>CHANGED</c> flag, not once a second
/// as the spec first said: they only cost anything when they change, and a once-a-second read-back
/// plus the ~590 ms seen just after load would breach the 1.5 s settle timeout.
/// </summary>
public sealed class SimConnectParameterGateway : ISimParameterGateway
{
    public const string ClientName = "Crowsnest";

    private const uint CameraId = 0;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ParameterRegistry _registry;
    private readonly Func<ISimConnectClient> _connect;
    private readonly ILogger<SimConnectParameterGateway> _log;
    private readonly TimeProvider _time;
    private readonly SimReadiness _readiness;
    private readonly Channel<ParameterSnapshot> _snapshots = Channel.CreateUnbounded<ParameterSnapshot>();
    private readonly BehaviorSubject<SimConnectionState> _state = new(SimConnectionState.Disconnected);
    private readonly ConcurrentDictionary<ParameterId, (int Value, long SentAt)> _inFlight = new();

    private readonly Lock _gate = new();
    private readonly List<SimSubscription> _subscriptions = [];

    // Owned by the RunAsync loop.
    private readonly Dictionary<ParameterId, int?> _latest = [];

    // Read by the writers, set by the loop.
    private volatile ISimConnectClient? _client;
    private volatile bool _ready;

    // Set by SubscribeAsync, cleared by the loop once it has re-sent the latest values.
    private volatile bool _replay;

    public SimConnectParameterGateway(
        ParameterRegistry registry,
        Func<ISimConnectClient> connect,
        ILogger<SimConnectParameterGateway> log,
        TimeProvider? time = null,
        TimeSpan? quiet = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(log);

        _registry = registry;
        _connect = connect;
        _log = log;
        _time = time ?? TimeProvider.System;
        _readiness = new SimReadiness(quiet ?? SimReadiness.DefaultQuiet);
    }

    public IObservable<SimConnectionState> ConnectionState => _state;

    public IAsyncEnumerable<ParameterSnapshot> Snapshots => _snapshots.Reader.ReadAllAsync();

    public Task SubscribeAsync(IReadOnlyList<SimSubscription> subscriptions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        lock (_gate)
        {
            foreach (SimSubscription subscription in subscriptions.Where(s => !_subscriptions.Any(known => known.Id == s.Id)))
            {
                _subscriptions.Add(subscription);
                _client?.WatchValue(IdOf(_subscriptions.Count - 1), subscription.Read.Name, subscription.Read.Unit, SimConnectPeriod.VisualFrame);
            }
        }

        // A new subscriber (a panel plugged back in) starts with no values, and SimConnect only
        // reports changes, so a cockpit that sits still would never fill it in. Ask the loop,
        // which owns the latest values, to send them all again.
        _replay = true;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ParameterId id, int canonicalValue, CancellationToken ct)
    {
        WriteBinding write = _registry[id].Write;
        if (write.Mode != WriteMode.KeyEvent)
        {
            throw new NotSupportedException($"'{id}' writes by {write.Mode}; only key events are implemented (spec §7.3).");
        }

        if (Transmit(write.Target, PayloadEncoder.Encode(canonicalValue, write.Encoding)))
        {
            _inFlight[id] = (canonicalValue, _time.GetTimestamp());
        }

        return Task.CompletedTask;
    }

    public Task InvokeAsync(string eventName, uint payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        Transmit(eventName, payload);
        return Task.CompletedTask;
    }

    /// <summary>Connects, and keeps reconnecting, until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = MinBackoff;
        bool announcedWaiting = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using ISimConnectClient client = _connect();
                try
                {
                    await client.OpenAsync(ClientName, ct).ConfigureAwait(false);
                }
                catch (SimConnectUnavailableException e)
                {
                    if (!announcedWaiting)
                    {
                        _log.LogInformation("Waiting for the sim: {Reason}", e.Message);
                        announcedWaiting = true;
                    }

                    await Task.Delay(Jitter(backoff), _time, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                    continue;
                }

                backoff = MinBackoff;
                announcedWaiting = false;
                await RunSessionAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "The sim session failed; reconnecting in {Backoff}", backoff);
                _state.OnNext(SimConnectionState.Faulted);
                if (!await DelayAsync(Jitter(backoff), ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            finally
            {
                _client = null;
                _ready = false;
            }
        }

        _state.OnNext(SimConnectionState.Disconnected);
    }

    public ValueTask DisposeAsync()
    {
        _snapshots.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task RunSessionAsync(ISimConnectClient client, CancellationToken ct)
    {
        _latest.Clear();
        _inFlight.Clear();
        _readiness.Reset(_time.GetUtcNow());
        _state.OnNext(SimConnectionState.Connecting);

        client.WatchValue(CameraId, "CAMERA STATE", "Enum", SimConnectPeriod.VisualFrame);
        lock (_gate)
        {
            _client = client;
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                client.WatchValue(IdOf(i), _subscriptions[i].Read.Name, _subscriptions[i].Read.Unit, SimConnectPeriod.VisualFrame);
            }
        }

        Task<bool>? readable = null;
        while (true)
        {
            readable ??= client.Messages.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(readable, Task.Delay(CheckInterval, _time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (readable.IsCompleted)
            {
                if (!await readable.ConfigureAwait(false))
                {
                    _log.LogWarning("The SimConnect session ended without a Quit.");
                    break;
                }

                readable = null;
                bool quit = false;
                while (!quit && client.Messages.TryRead(out SimConnectMessage? message))
                {
                    quit = Handle(message);
                }

                if (quit)
                {
                    _log.LogInformation("The sim is shutting down.");
                    break;
                }
            }

            if (_readiness.Check(_time.GetUtcNow()))
            {
                BecomeReady();
            }
            else if (_ready && _replay)
            {
                _replay = false;
                PublishLatest();
            }
        }

        _state.OnNext(SimConnectionState.Disconnected);
    }

    /// <returns>True when the session is over.</returns>
    private bool Handle(SimConnectMessage message)
    {
        DateTimeOffset now = _time.GetUtcNow();

        switch (message)
        {
            case SimConnectMessage.Opened opened:
                _log.LogInformation("Connected to {Application} {Version}; waiting for the flight to be ready", opened.ApplicationName, opened.Version);
                break;

            case SimConnectMessage.Value { Id: CameraId } camera:
                if (_readiness.OnCamera((int)camera.Raw, now))
                {
                    _ready = false;
                    _log.LogInformation("Camera {Camera} is not a flying view; holding values until the next flight is ready", (int)camera.Raw);
                    _state.OnNext(SimConnectionState.Connecting);
                }

                break;

            case SimConnectMessage.Value value when SubscriptionOf(value.Id) is { } subscription:
                OnValue(subscription, value.Raw, now);
                break;

            case SimConnectMessage.Failed failed:
                _log.LogWarning("SimConnect rejected call {SendId} (argument {Index}): {Exception}", failed.SendId, failed.Index, failed.Exception);
                break;

            case SimConnectMessage.Quit:
                return true;
        }

        return false;
    }

    private void OnValue(SimSubscription subscription, double raw, DateTimeOffset now)
    {
        int? value = ValueConverter.ToCanonical(raw, subscription.Read);
        if (_latest.TryGetValue(subscription.Id, out int? known) && known == value)
        {
            return;
        }

        _latest[subscription.Id] = value;
        if (!_ready)
        {
            _readiness.OnUnsolicitedChange(now);
            return;
        }

        if (value is { } v && _inFlight.TryGetValue(subscription.Id, out (int Value, long SentAt) sent) && sent.Value == v)
        {
            _inFlight.TryRemove(subscription.Id, out _);
            _log.LogInformation("{Id} read back {Value} in {Ms:F1} ms", subscription.Id, v, _time.GetElapsedTime(sent.SentAt).TotalMilliseconds);
        }

        Publish(subscription.Id, value);
    }

    private void BecomeReady()
    {
        _log.LogInformation("The flight is ready: a flying camera and {Quiet} s with no unprompted changes", _readiness.Quiet.TotalSeconds);
        _replay = false;
        PublishLatest();

        _ready = true;
        _state.OnNext(SimConnectionState.Connected);
    }

    private void PublishLatest()
    {
        foreach ((ParameterId id, int? value) in _latest)
        {
            Publish(id, value);
        }
    }

    private void Publish(ParameterId id, int? value) =>
        _snapshots.Writer.TryWrite(new ParameterSnapshot(id, value ?? 0, Available: value is not null));

    /// <returns>False if the event was dropped because the flight is not ready.</returns>
    private bool Transmit(string eventName, uint payload)
    {
        if (!_ready || _client is not { } client)
        {
            // The rule: write nothing until the flight is ready. The session's settle timeout
            // shows the pilot it did not land.
            _log.LogDebug("Dropped {Event} {Payload}: the flight is not ready", eventName, payload);
            return false;
        }

        client.TransmitEvent(eventName, payload);
        return true;
    }

    private SimSubscription? SubscriptionOf(uint id)
    {
        lock (_gate)
        {
            int index = (int)id - 1;
            return index >= 0 && index < _subscriptions.Count ? _subscriptions[index] : null;
        }
    }

    private static uint IdOf(int index) => (uint)index + 1;

    /// <returns>False if cancelled.</returns>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static TimeSpan Jitter(TimeSpan backoff) =>
        backoff * (0.8 + (Random.Shared.NextDouble() * 0.4));
}
