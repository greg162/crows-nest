namespace Crowsnest.Core.Application.Ports;

/// <summary>
/// A device (one physical board), as Core sees it (spec §5.6). Core knows nothing of serial ports,
/// JSON or USB descriptors — that is Crowsnest.Device's problem.
/// </summary>
public interface IDevice : IAsyncDisposable
{
    IObservable<DeviceConnectionState> ConnectionState { get; }

    /// <summary>Populated by the hello handshake; null until the device has identified itself.</summary>
    DeviceCapabilities? Capabilities { get; }

    IAsyncEnumerable<DeviceInputEvent> Inputs { get; }

    Task RenderAsync(DisplayFrame frame, CancellationToken ct);
}

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,
    Faulted,
}

public enum ScreenShape
{
    Round,
    Rectangular,
}

/// <summary>
/// What the device reported it can do. The host adapts to the device, not the reverse: once
/// capability-driven layout selection exists (spec §5.7, not yet built), a board with no touch
/// will never receive a page whose only swap gesture is a tap.
/// </summary>
public sealed record DeviceCapabilities(
    string DeviceType,
    string FirmwareVersion,
    ScreenShape Shape,
    int Width,
    int Height,
    bool HasEncoder,
    int DetentsPerClick,
    bool HasTouch,
    int ButtonCount,
    int MaxFields,
    IReadOnlyList<PageLayout> Layouts);

/// <summary>
/// Stable identity for a device (spec §6.2). The eFuse MAC survives reflash, replug and
/// hub port renumbering; nothing in the system may key off a COM port name.
/// </summary>
public sealed record DeviceIdentity(
    string HardwareId,
    string DeviceType,
    string FirmwareVersion)
{
    /// <summary>The last six characters of the hardware id: what the device's screen shows and settings accept.</summary>
    public string ShortId => ShortIdOf(HardwareId);

    public static string ShortIdOf(string hardwareId)
    {
        ArgumentNullException.ThrowIfNull(hardwareId);
        return hardwareId.Length > 6 ? hardwareId[^6..] : hardwareId;
    }
}

public abstract record DeviceInputEvent(long Sequence, DateTimeOffset At)
{
    public sealed record EncoderTurned(long Sequence, DateTimeOffset At, int Detents)
        : DeviceInputEvent(Sequence, At);

    public sealed record KnobPressed(long Sequence, DateTimeOffset At, PressKind Kind)
        : DeviceInputEvent(Sequence, At);

    public sealed record ScreenTapped(long Sequence, DateTimeOffset At, int X, int Y)
        : DeviceInputEvent(Sequence, At);

    public sealed record SwipeDetected(long Sequence, DateTimeOffset At, SwipeDir Dir)
        : DeviceInputEvent(Sequence, At);
}

public enum PressKind
{
    Short,
    Long,
}

public enum SwipeDir
{
    Left,
    Right,
    Up,
    Down,
}
