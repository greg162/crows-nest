namespace Crowsnest.Core.Panels.Com;

/// <summary>
/// COM channel spacing (spec §5.1, §5.3). Only COM radios have a spacing mode; NAV is a
/// flat 50 kHz <c>LinearGrid</c>.
/// </summary>
public enum ChannelSpacing
{
    /// <summary>Legacy spacing: .000 .025 .050 .075 in every 100 kHz block.</summary>
    TwentyFiveKhz,

    /// <summary>
    /// 8.33 kHz spacing, dialled by ICAO channel name rather than by true frequency. A
    /// superset of <see cref="TwentyFiveKhz"/>: the 25 kHz names stay legal.
    /// </summary>
    EightPointThreeThree,
}
