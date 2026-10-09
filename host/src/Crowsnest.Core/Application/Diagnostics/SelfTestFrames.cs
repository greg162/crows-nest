using Crowsnest.Core.Application.Ports;

namespace Crowsnest.Core.Application.Diagnostics;

/// <summary>
/// Frames with no sim behind them, for bringing a device up and for the tray's
/// "test this device" action. They exercise each <see cref="PageLayout"/> the firmware
/// implements, which is what makes them worth having in Core rather than in a tool:
/// a board port is correct when it renders these four the way the reference device does.
/// </summary>
public static class SelfTestFrames
{
    /// <summary>
    /// The bring-up frame. If this lands on the glass, the whole path works: Core built it,
    /// the codec wrote it, the link carried it and the firmware rendered it.
    /// </summary>
    public static DisplayFrame HelloWorld(long revision = 1) =>
        new(
            Revision: revision,
            Sim: SimConnectionState.Disconnected,
            Page: new PageDescriptor("selftest", "CROWSNEST", PageLayout.SingleValue, Index: 0, Count: 4),
            Fields:
            [
                new FieldDescriptor(FieldRole.Primary, "LINK", "HELLO WORLD"),
            ]);

    /// <summary>
    /// A COM 1 page with no sim attached. The cursor span covers the kHz digits of
    /// "121.500", which is the spec's worked example of a [start, end) range (§5.2).
    /// </summary>
    public static DisplayFrame ComExample(long revision = 2) =>
        new(
            Revision: revision,
            Sim: SimConnectionState.Disconnected,
            Page: new PageDescriptor("com1", "COM 1", PageLayout.ActiveStandbyPair, Index: 1, Count: 4),
            Fields:
            [
                new FieldDescriptor(FieldRole.Primary, "STBY", "121.500", CursorSpan: 4..7, Pending: true),
                new FieldDescriptor(FieldRole.Secondary, "ACTIVE", "122.800"),
            ]);

    /// <summary>Two independent values on one screen — the layout NAV and the autopilot want.</summary>
    public static DisplayFrame DualExample(long revision = 3) =>
        new(
            Revision: revision,
            Sim: SimConnectionState.Disconnected,
            Page: new PageDescriptor("selftest.dual", "SELF TEST", PageLayout.DualValue, Index: 2, Count: 4),
            Fields:
            [
                new FieldDescriptor(FieldRole.Primary, "ALT", "12,000", CursorSpan: 0..2),
                new FieldDescriptor(FieldRole.Secondary, "HDG", "270"),
            ]);

    /// <summary>One value in the P180 frame, the transponder's layout, its cursor on the last digit.</summary>
    public static DisplayFrame FramedExample(long revision = 4) =>
        new(
            Revision: revision,
            Sim: SimConnectionState.Disconnected,
            Page: new PageDescriptor("selftest.framed", "XPDR", PageLayout.FramedValue, Index: 3, Count: 4),
            Fields:
            [
                new FieldDescriptor(FieldRole.Primary, "SQUAWK", "7000", CursorSpan: 3..4),
            ]);

    /// <summary>All four, in page order, for a sweep across the layouts during bring-up.</summary>
    public static IReadOnlyList<DisplayFrame> All(long firstRevision = 1) =>
    [
        HelloWorld(firstRevision),
        ComExample(firstRevision + 1),
        DualExample(firstRevision + 2),
        FramedExample(firstRevision + 3),
    ];
}
