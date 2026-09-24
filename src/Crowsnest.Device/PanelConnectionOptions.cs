namespace Crowsnest.Device;

/// <summary>Timings for the link (spec §6.1). Defaults are the values the spec names.</summary>
public sealed record PanelConnectionOptions
{
    /// <summary>Announced to the device in the host hello.</summary>
    public string HostName { get; init; } = "Crowsnest 0.1.0";

    /// <summary>How long to wait for a hello before giving up on a candidate port.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a single <c>MeasureRoundTripAsync</c> waits for its matching pong. The
    /// spec's budget is a 20 ms median, so this is only ever hit by a fault.
    /// </summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Missed pongs before the link is declared faulted.</summary>
    public int MissedPongLimit { get; init; } = 3;

    public int Brightness { get; init; } = 80;

    public string Theme { get; init; } = "day";
}
