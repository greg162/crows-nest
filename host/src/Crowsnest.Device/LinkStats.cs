namespace Crowsnest.Device;

/// <summary>How one device's link is doing, for the diagnostics window (spec §8).</summary>
/// <param name="ConnectedAt">When the handshake completed. A restart under an open port does not reset it.</param>
/// <param name="FramesIn">Every frame read from the device, including ones that were ignored.</param>
/// <param name="FramesOut">Every frame sent to the device: screens, pings, acks.</param>
/// <param name="FramesIgnored">Frames that were malformed or of a type this host does not know (spec §6.1).</param>
/// <param name="Restarts">Times the device said hello again under an open port: a crash or its reset button.</param>
/// <param name="RoundTrip">The last heartbeat's ping to pong; null until the first comes back.</param>
public sealed record LinkStats(
    DateTimeOffset ConnectedAt,
    long FramesIn,
    long FramesOut,
    long FramesIgnored,
    int Restarts,
    TimeSpan? RoundTrip);
