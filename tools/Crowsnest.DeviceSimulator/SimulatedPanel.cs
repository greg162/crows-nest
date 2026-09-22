using Crowsnest.Device.Protocol;
using Crowsnest.Device.Transport;

namespace Crowsnest.DeviceSimulator;

/// <summary>
/// The device half of the link, in C# (spec §11). It answers the handshake, pongs the
/// heartbeat and surfaces every state frame it is told to render, so the whole PC side is
/// developable before firmware exists — and so a real panel can be swapped in to work out
/// which end broke.
///
/// It deliberately mirrors what the firmware's <c>crowsnest_link</c> does, including
/// discarding any frame whose revision is not newer than the last one applied.
/// </summary>
public sealed class SimulatedPanel : IAsyncDisposable
{
    private readonly IDeviceTransport _transport;
    private readonly NdjsonFrameReader _reader;
    private readonly NdjsonFrameWriter _writer;
    private readonly WireCapabilities _capabilities;
    private readonly string _hardwareId;
    private readonly string _firmwareVersion;

    private long _sequence;
    private long _appliedRevision = -1;

    public SimulatedPanel(
        IDeviceTransport transport,
        string hardwareId = "a4cb8fdccc6c",
        string firmwareVersion = "0.1.0-sim")
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _reader = new NdjsonFrameReader(transport.Input);
        _writer = new NdjsonFrameWriter(transport.Output);
        _hardwareId = hardwareId;
        _firmwareVersion = firmwareVersion;

        // The reference CrowPanel 2.1" (spec §9.5). A board port changes these numbers
        // and nothing else on this side.
        _capabilities = new WireCapabilities
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
        };
    }

    /// <summary>Raised for each state frame the panel accepts.</summary>
    public Action<HostState>? Rendered { get; set; }

    /// <summary>Raised for notices, which the real firmware shows as a banner.</summary>
    public Action<HostNotice>? NoticeShown { get; set; }

    /// <summary>Frames rejected as stale, i.e. not newer than the last applied revision.</summary>
    public long StaleFramesDropped { get; private set; }

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
                    await SendHelloAsync(ct);
                    break;

                case HostPing ping:
                    await _writer.WriteAsync(new DevicePong { Timestamp = ping.Timestamp }, ct);
                    break;

                case HostState state:
                    if (state.Revision <= _appliedRevision)
                    {
                        StaleFramesDropped++;
                        break;
                    }

                    _appliedRevision = state.Revision;
                    Rendered?.Invoke(state);
                    break;

                case HostNotice notice:
                    NoticeShown?.Invoke(notice);
                    break;

                default:
                    // hello_ack and anything this firmware version has not heard of.
                    break;
            }
        }
    }

    public ValueTask TurnEncoderAsync(int detents, CancellationToken ct = default) =>
        _writer.WriteAsync(
            new DeviceInputFrame { Sequence = NextSequence(), Event = "encoder", Detents = detents },
            ct);

    public ValueTask PressKnobAsync(bool held = false, CancellationToken ct = default) =>
        _writer.WriteAsync(
            new DeviceInputFrame { Sequence = NextSequence(), Event = "press", Kind = held ? "long" : "short" },
            ct);

    public ValueTask TapAsync(int x, int y, CancellationToken ct = default) =>
        _writer.WriteAsync(
            new DeviceInputFrame { Sequence = NextSequence(), Event = "tap", X = x, Y = y },
            ct);

    private ValueTask SendHelloAsync(CancellationToken ct) =>
        _writer.WriteAsync(
            new DeviceHello
            {
                DeviceType = "crowpanel-2.1-rotary",
                FirmwareVersion = _firmwareVersion,
                HardwareId = _hardwareId,
                Capabilities = _capabilities,
            },
            ct);

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
