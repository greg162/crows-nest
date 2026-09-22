using System.Text.Json;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Ports;

namespace Crowsnest.Device.Protocol;

/// <summary>
/// Translates between Core's display contract and the wire types. This is the only
/// place that knows the protocol's spelling of a layout or a connection state.
/// </summary>
public static class ProtocolCodec
{
    public static HostState ToWire(DisplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return new HostState
        {
            Revision = frame.Revision,
            Ack = frame.AckSequence,
            Sim = ToWire(frame.Sim),
            Page = new WirePage
            {
                Id = frame.Page.Id,
                Title = frame.Page.Title,
                Layout = ToWire(frame.Page.Layout),
                Index = frame.Page.Index,
                Count = frame.Page.Count,
            },
            Fields = [.. frame.Fields.Select(ToWire)],
        };
    }

    public static WireField ToWire(FieldDescriptor field)
    {
        ArgumentNullException.ThrowIfNull(field);

        int[]? cursor = null;
        if (field.CursorSpan is { } span)
        {
            (int offset, int length) = span.GetOffsetAndLength(field.Text.Length);
            cursor = [offset, offset + length];
        }

        return new WireField
        {
            Role = ToWire(field.Role),
            Label = field.Label,
            Text = field.Text,
            Cursor = cursor,
            Pending = field.Pending,
        };
    }

    public static string ToWire(PageLayout layout) => layout switch
    {
        PageLayout.ActiveStandbyPair => "pair",
        PageLayout.SingleValue => "single",
        PageLayout.DualValue => "dual",
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    public static string ToWire(FieldRole role) => role switch
    {
        FieldRole.Primary => "primary",
        FieldRole.Secondary => "secondary",
        FieldRole.Tertiary => "tertiary",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static string ToWire(SimConnectionState state) => state switch
    {
        SimConnectionState.Disconnected => "disconnected",
        SimConnectionState.Connecting => "connecting",
        SimConnectionState.Connected => "connected",
        SimConnectionState.Faulted => "faulted",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static PageLayout? ParseLayout(string? wire) => wire switch
    {
        "pair" => PageLayout.ActiveStandbyPair,
        "single" => PageLayout.SingleValue,
        "dual" => PageLayout.DualValue,
        _ => null,
    };

    public static DeviceCapabilities ToCapabilities(DeviceHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);

        WireCapabilities caps = hello.Capabilities;
        return new DeviceCapabilities(
            DeviceType: hello.DeviceType,
            FirmwareVersion: hello.FirmwareVersion,
            Shape: caps.Shape == "round" ? ScreenShape.Round : ScreenShape.Rectangular,
            Width: caps.Width,
            Height: caps.Height,
            HasEncoder: caps.Encoder,
            DetentsPerClick: caps.DetentsPerClick,
            HasTouch: caps.Touch,
            ButtonCount: caps.Buttons,
            MaxFields: caps.MaxFields,
            // Unknown layout names are dropped rather than rejected: a future firmware may
            // advertise a layout this host has never heard of.
            Layouts: [.. caps.Layouts.Select(ParseLayout).OfType<PageLayout>()]);
    }

    public static DeviceIdentity ToIdentity(DeviceHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        return new DeviceIdentity(hello.HardwareId, hello.DeviceType, hello.FirmwareVersion);
    }

    /// <summary>
    /// Returns null for input frames this host does not understand, rather than throwing —
    /// unknown events are ignored so the two sides version independently (spec §6.1).
    /// </summary>
    public static DeviceInputEvent? ToInputEvent(DeviceInputFrame frame, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.Event switch
        {
            "encoder" => new DeviceInputEvent.EncoderTurned(frame.Sequence, at, frame.Detents),
            "press" => new DeviceInputEvent.KnobPressed(
                frame.Sequence, at, frame.Kind == "long" ? PressKind.Long : PressKind.Short),
            "tap" => new DeviceInputEvent.ScreenTapped(frame.Sequence, at, frame.X, frame.Y),
            "swipe" when ParseSwipe(frame.Direction) is { } dir
                => new DeviceInputEvent.SwipeDetected(frame.Sequence, at, dir),
            _ => null,
        };
    }

    private static SwipeDir? ParseSwipe(string? wire) => wire switch
    {
        "left" => SwipeDir.Left,
        "right" => SwipeDir.Right,
        "up" => SwipeDir.Up,
        "down" => SwipeDir.Down,
        _ => null,
    };

    /// <summary>
    /// Decodes one device frame. Returns false for malformed JSON, an unknown message type
    /// or a frame from a future protocol version — all of which are ignored, never fatal.
    /// </summary>
    public static bool TryDecodeDevice(ReadOnlySpan<byte> utf8Json, out DeviceMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize(utf8Json, ProtocolJsonContext.Default.DeviceMessage);
            return message is not null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>The device half of the same rule, used by the simulator.</summary>
    public static bool TryDecodeHost(ReadOnlySpan<byte> utf8Json, out HostMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize(utf8Json, ProtocolJsonContext.Default.HostMessage);
            return message is not null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            message = null;
            return false;
        }
    }
}
