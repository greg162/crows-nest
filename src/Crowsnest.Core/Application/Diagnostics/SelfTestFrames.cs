using Crowsnest.Core.Application.Ports;

namespace Crowsnest.Core.Application.Diagnostics;

/// <summary>
/// Frames with no sim behind them, for bringing a panel up and for the tray's
/// "test this panel" action. They exercise each <see cref="PageLayout"/> the firmware
/// implements, which is what makes them worth having in Core rather than in a tool:
/// a board port is correct when it renders these three the way the reference panel does.
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
            Page: new PageDescriptor("selftest", "CROWSNEST", PageLayout.SingleValue, Index: 0, Count: 3),
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
            Page: new PageDescriptor("com1", "COM 1", PageLayout.ActiveStandbyPair, Index: 1, Count: 3),
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
            Page: new PageDescriptor("selftest.dual", "SELF TEST", PageLayout.DualValue, Index: 2, Count: 3),
            Fields:
            [
                new FieldDescriptor(FieldRole.Primary, "ALT", "12,000", CursorSpan: 0..2),
                new FieldDescriptor(FieldRole.Secondary, "HDG", "270"),
            ]);

    /// <summary>All three, in page order, for a sweep across the layouts during bring-up.</summary>
    public static IReadOnlyList<DisplayFrame> All(long firstRevision = 1) =>
    [
        HelloWorld(firstRevision),
        ComExample(firstRevision + 1),
        DualExample(firstRevision + 2),
    ];
}
