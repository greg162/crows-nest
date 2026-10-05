using Crowsnest.Device.Protocol;
using Crowsnest.Device.Transport;

namespace Crowsnest.Device.Simulation;

/// <summary>
/// The device half of the link, in C# (spec §11). It answers the handshake, pongs the
/// heartbeat and surfaces every state frame it is told to render, so the whole PC side is
/// developable and testable without a board, and so a real device can be swapped in to work
/// out which end broke. The device simulator draws it on the console; the tests use one or
/// several, each with its own hardware id.
///
/// It deliberately mirrors what the firmware's <c>crowsnest_link</c> does, including
/// discarding any frame whose revision is not newer than the last one applied.
/// </summary>
public sealed class SimulatedDevice : IAsyncDisposable
{
    private readonly IDeviceTransport _transport;
    private readonly NdjsonFrameReader _reader;
    private readonly NdjsonFrameWriter _writer;
    private readonly string _firmwareVersion;

    private long _sequence;
    private long _appliedRevision = -1;
    private int _helloAcks;
    private int _brightness = -1;
    private long _staleFramesDropped;
    private volatile HostState? _latest;

    public SimulatedDevice(IDeviceTransport transport, string hardwareId = "a4cb8fdccc6c", string firmwareVersion = "0.1.0-sim")
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(hardwareId);

        _transport = transport;
        _reader = new NdjsonFrameReader(transport.Input);
        _writer = new NdjsonFrameWriter(transport.Output);
        HardwareId = hardwareId;
        _firmwareVersion = firmwareVersion;
    }

    public string HardwareId { get; }

    /// <summary>Raised for each state frame the device accepts, on the device's read loop.</summary>
    public Action<HostState>? Rendered { get; set; }

    /// <summary>The last state frame the device accepted: what its screen shows.</summary>
    public HostState? Latest => _latest;

    /// <summary>Raised for notices, which the real firmware shows as a banner.</summary>
    public Action<HostNotice>? NoticeShown { get; set; }

    /// <summary>Frames rejected as stale, i.e. not newer than the last applied revision.</summary>
    public long StaleFramesDropped => Interlocked.Read(ref _staleFramesDropped);

    /// <summary>How many <c>hello_ack</c>s have arrived: one per handshake, and one per config refresh.</summary>
    public int HelloAcks => Volatile.Read(ref _helloAcks);

    /// <summary>From the last <c>hello_ack</c>; -1 before the first.</summary>
    public int Brightness => Volatile.Read(ref _brightness);

    /// <summary>
    /// Fault injection: answer pings with the timestamp pinned to <c>int.MaxValue</c>, as the
    /// firmware did while <c>ts</c> was an <c>int32_t</c> (found at first bring-up).
    /// </summary>
    public bool SaturatePongTimestampToInt32 { get; set; }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _transport.ConnectAsync(ct);

        await foreach (byte[] frame in _reader.ReadAllAsync(ct))
        {
            if (!ProtocolCodec.TryDecodeHost(frame, out HostMessage? message) || message is null)
            {
                continue;
            }

            switch (message)
            {
                case HostHello:
                    await SendHelloAsync(ct: ct);
                    break;

                case HostHelloAck ack:
                    Volatile.Write(ref _brightness, ack.Config?.Brightness ?? -1);
                    Interlocked.Increment(ref _helloAcks);
                    break;

                case HostPing ping:
                    long timestamp = SaturatePongTimestampToInt32 ? Math.Min(ping.Timestamp, int.MaxValue) : ping.Timestamp;
                    await _writer.WriteAsync(new DevicePong { Timestamp = timestamp }, ct);
                    break;

                case HostState state:
                    if (state.Revision <= _appliedRevision)
                    {
                        Interlocked.Increment(ref _staleFramesDropped);
                        break;
                    }

                    _appliedRevision = state.Revision;
                    _latest = state;
                    Rendered?.Invoke(state);
                    break;

                case HostNotice notice:
                    NoticeShown?.Invoke(notice);
                    break;

                default:
                    // Anything this firmware version has not heard of.
                    break;
            }
        }
    }

    /// <summary>
    /// The hello the device sends in reply to the host's, and unasked when it restarts under an
    /// open port. A different <paramref name="hardwareId"/> pretends another board took its place.
    /// </summary>
    public ValueTask SendHelloAsync(string? hardwareId = null, CancellationToken ct = default)
    {
        // A restart numbers frames afresh, as the firmware does on a hello.
        _appliedRevision = -1;
        return _writer.WriteAsync(
            new DeviceHello
            {
                DeviceType = "crowpanel-2.1-rotary",
                FirmwareVersion = _firmwareVersion,
                HardwareId = hardwareId ?? HardwareId,

                // The reference CrowPanel 2.1" (spec §9.5). A board port changes these numbers
                // and nothing else on this side.
                Capabilities = new WireCapabilities
                {
                    Shape = "round",
                    Width = 480,
                    Height = 480,
                    Encoder = true,
                    DetentsPerClick = 4,
                    Touch = true,
                    Buttons = 1,
                    MaxFields = 3,
                    Layouts = ["pair", "single", "dual"],
                },
            },
            ct);
    }

    public ValueTask TurnEncoderAsync(int detents, CancellationToken ct = default) =>
        _writer.WriteAsync(new DeviceInputFrame { Sequence = NextSequence(), Event = "encoder", Detents = detents }, ct);

    public ValueTask PressKnobAsync(bool held = false, CancellationToken ct = default) =>
        _writer.WriteAsync(new DeviceInputFrame { Sequence = NextSequence(), Event = "press", Kind = held ? "long" : "short" }, ct);

    public ValueTask TapAsync(int x, int y, CancellationToken ct = default) =>
        _writer.WriteAsync(new DeviceInputFrame { Sequence = NextSequence(), Event = "tap", X = x, Y = y }, ct);

    /// <summary>Fault injection: bytes straight onto the link, framing and all.</summary>
    public async Task SendRawAsync(string text)
    {
        await _transport.Output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
        await _transport.Output.FlushAsync();
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
