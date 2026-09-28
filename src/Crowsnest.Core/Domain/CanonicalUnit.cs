namespace Crowsnest.Core.Domain;

/// <summary>
/// The unit a parameter's int is held in (spec §5.0, §5.1). Registry keys in brackets.
/// </summary>
public enum CanonicalUnit
{
    /// <summary>Frequencies (<c>kHz</c>).</summary>
    Kilohertz,

    /// <summary>Altitude (<c>ft</c>).</summary>
    Feet,

    /// <summary>Heading and course (<c>deg</c>).</summary>
    Degrees,

    /// <summary>Vertical speed (<c>fpm</c>).</summary>
    FeetPerMinute,

    /// <summary>Airspeed (<c>kt</c>).</summary>
    Knots,

    /// <summary>Barometer (<c>mb</c>).</summary>
    Millibars,

    /// <summary>A squawk as it reads: 7700 is the int 7700 (<c>code</c>).</summary>
    OctalCode,
}
