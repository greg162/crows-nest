using System.Collections.Concurrent;
using System.Threading.Channels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device.Internal;
using Crowsnest.Device.Protocol;
using Crowsnest.Device.Transport;

namespace Crowsnest.Device;

/// <summary>
/// One panel: transport, codec and heartbeat (spec §6). Implements Core's port, so the
/// coordinator above it never learns whether this is a real board or the simulator.
/// </summary>
public sealed class PanelDeviceConnection : IPanelDevice
{
    private readonly IDeviceTransport _transport;
    private readonly PanelConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly BehaviorSubject<DeviceConnectionState> _state = new(DeviceConnectionState.Disconnected);
    private readonly Channel<DeviceInputEvent> _inputs =
        Channel.CreateUnbounded<DeviceInputEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<DeviceHello> _hello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _pendingPings = new();
    private readonly CancellationTokenSource _shutdown = new();

    /* A transport's pipes do not exist until it has connected — SerialPortTransport has no
     * BaseStream before Open. These are therefore built in ConnectAsync, not here. */
    private NdjsonFrameReader? _reader;
    private NdjsonFrameWriter? _writer;
    private Task? _readLoop;
    private Task? _heartbeat;
    private long _outstandingPings;
    private bool _disposed;

    public PanelDeviceConnection(
        IDeviceTransport transport,
        PanelConnectionOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _options = options ?? new PanelConnectionOptions();
        _time = time ?? TimeProvider.System;
    }

    private NdjsonFrameWriter Writer =>
        _writer ?? throw new InvalidOperationException("Call ConnectAsync before using the link.");

    public IObservable<DeviceConnectionState> ConnectionState => _state;

    public DeviceCapabilities? Capabilities { get; private set; }

    /// <summary>Stable panel identity from the hello frame; null until the handshake completes.</summary>
    public DeviceIdentity? Identity { get; private set; }

    /// <summary>Firmware log lines arriving over the link. Diagnostics only.</summary>
    public Action<DeviceLog>? LogReceived { get; set; }

    public IAsyncEnumerable<DeviceInputEvent> Inputs => _inputs.Reader.ReadAllAsync(_shutdown.Token);

    /// <summary>
    /// Opens the transport and completes the hello handshake. Throws
    /// <see cref="TimeoutException"/> when the device never identifies itself, which is how
    /// port probing rejects a candidate that is not a panel.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _state.OnNext(DeviceConnectionState.Connecting);
        await _transport.ConnectAsync(ct);

        // Only now do the transport's pipes exist.
        _reader = new NdjsonFrameReader(_transport.Input);
        _writer = new NdjsonFrameWriter(_transport.Output);

        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);

        _state.OnNext(DeviceConnectionState.Handshaking);
        await Writer.WriteAsync(new HostHello { Host = _options.HostName }, ct);

        DeviceHello hello;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token))
        {
            handshake.CancelAfter(_options.HandshakeTimeout);
            try
            {
                hello = await _hello.Task.WaitAsync(handshake.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _state.OnNext(DeviceConnectionState.Faulted);
                throw new TimeoutException(
                    $"No hello frame within {_options.HandshakeTimeout.TotalMilliseconds:F0} ms. Not a Crowsnest panel, or its firmware is not speaking.");
            }
        }

        Capabilities = ProtocolCodec.ToCapabilities(hello);
        Identity = ProtocolCodec.ToIdentity(hello);

        await Writer.WriteAsync(
            new HostHelloAck
            {
                Config = new HostConfig { Brightness = _options.Brightness, Theme = _options.Theme },
            },
            ct);

        _state.OnNext(DeviceConnectionState.Connected);
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None);
    }

    public Task RenderAsync(DisplayFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Writer.WriteAsync(ProtocolCodec.ToWire(frame), ct).AsTask();
    }

    public Task NoticeAsync(string kind, CancellationToken ct) =>
        Writer.WriteAsync(new HostNotice { Kind = kind }, ct).AsTask();

    /// <summary>
    /// Round-trip time for one ping. Spike 0(c) requires this under 20 ms over USB CDC,
    /// and the tray's diagnostics page reports it.
    /// </summary>
    public async Task<TimeSpan> MeasureRoundTripAsync(CancellationToken ct)
    {
        long stamp = _time.GetTimestamp();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingPings.TryAdd(stamp, pending))
        {
            // Two pings within one timestamp tick; the caller can simply ask again.
            throw new InvalidOperationException("A round-trip measurement is already in flight for this timestamp.");
        }

        try
        {
            await Writer.WriteAsync(new HostPing { Timestamp = stamp }, ct);
            await pending.Task.WaitAsync(ct);
            return _time.GetElapsedTime(stamp);
        }
        finally
        {
            _pendingPings.TryRemove(stamp, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (byte[] frame in _reader!.ReadAllAsync(ct))
            {
                if (ProtocolCodec.TryDecodeDevice(frame, out DeviceMessage? message) && message is not null)
                {
                    Dispatch(message);
                }

                // Malformed frames and unknown message types are ignored by design (spec §6.1).
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _state.OnNext(DeviceConnectionState.Faulted);
        }
        finally
        {
            _inputs.Writer.TryComplete();
            _hello.TrySetCanceled(CancellationToken.None);
        }
    }

    private void Dispatch(DeviceMessage message)
    {
        switch (message)
        {
            case DeviceHello hello:
                // A second hello means the panel rebooted; take the newer capabilities.
                if (!_hello.TrySetResult(hello))
                {
                    Capabilities = ProtocolCodec.ToCapabilities(hello);
                    Identity = ProtocolCodec.ToIdentity(hello);
                }

                break;

            case DeviceInputFrame input:
                if (ProtocolCodec.ToInputEvent(input, _time.GetUtcNow()) is { } evt)
                {
                    _inputs.Writer.TryWrite(evt);
                }

                break;

            case DevicePong pong:
                Interlocked.Exchange(ref _outstandingPings, 0);
                if (_pendingPings.TryRemove(pong.Timestamp, out TaskCompletionSource? waiter))
                {
                    waiter.TrySetResult();
                }

                break;

            case DeviceLog log:
                LogReceived?.Invoke(log);
                break;

            default:
                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.PingInterval, _time, ct);

                if (Interlocked.Increment(ref _outstandingPings) > _options.MissedPongLimit)
                {
                    _state.OnNext(DeviceConnectionState.Faulted);
                    return;
                }

                await Writer.WriteAsync(
                    new HostPing { Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds() },
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _state.OnNext(DeviceConnectionState.Faulted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent on purpose: a caller that tears the link down explicitly and then
        // leaves an `await using` to run must not get an ObjectDisposedException for it.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync();

        // Close the transport BEFORE waiting on the read loop. A SerialPort read that is
        // already blocked in the driver ignores the cancellation token entirely — only
        // closing the port underneath it makes the read return. Waiting first deadlocks.
        await _transport.DisposeAsync();

        foreach (Task? task in new[] { _readLoop, _heartbeat })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                // Bounded even so: a transport that cannot be closed must not take the
                // whole application down with it on shutdown.
                await task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                // Expected on teardown.
            }
        }

        _state.OnNext(DeviceConnectionState.Disconnected);
        _state.OnCompleted();
        _shutdown.Dispose();
    }
}
