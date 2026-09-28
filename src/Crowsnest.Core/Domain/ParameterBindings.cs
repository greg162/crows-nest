namespace Crowsnest.Core.Domain;

/// <summary>
/// Where a parameter's value is read from in the sim (spec §5.2). The gateway subscribes to
/// every read binding in one data definition (§7.3).
/// </summary>
/// <param name="Name">The SimVar, e.g. <c>"COM STANDBY FREQUENCY:1"</c>.</param>
/// <param name="Unit">The SimConnect unit to request it in, e.g. <c>"Hz"</c>.</param>
/// <param name="Scale">Multiplies the sim's value into the canonical unit before rounding: 0.001 for Hz → kHz.</param>
public sealed record ReadBinding(ReadSource Source, string Name, string Unit, double Scale);

public enum ReadSource
{
    SimVar,
    LVar,
    Calculator,
}

/// <summary>How a new value is sent to the sim (spec §5.2, §7.3).</summary>
/// <param name="Target">The key event or variable, e.g. <c>"COM_STBY_RADIO_SET_HZ"</c>.</param>
public sealed record WriteBinding(WriteMode Mode, string Target, PayloadEncoding Encoding);

public enum WriteMode
{
    /// <summary>Preferred: runs the aircraft's own systems logic (§7.3).</summary>
    KeyEvent,

    SimVarWrite,
    LVarWrite,
}

public enum PayloadEncoding
{
    /// <summary>kHz × 1000, the only way to set a third decimal place.</summary>
    Hz,

    Bcd16,

    /// <summary>The canonical value as it is.</summary>
    Raw,

    /// <summary>The canonical value as a signed 32-bit payload, for vertical speed.</summary>
    Signed,
}

/// <summary>
/// One value the gateway subscribes to (spec §7.3): every parameter's read binding, plus the
/// watched values a panel behaviour needs but nobody tunes, such as <c>com1.spacing</c>.
/// Snapshots come back under <paramref name="Id"/>.
/// </summary>
public sealed record SimSubscription(ParameterId Id, ReadBinding Read);
