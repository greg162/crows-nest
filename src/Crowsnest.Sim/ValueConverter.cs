using Crowsnest.Core.Domain;

namespace Crowsnest.Sim;

/// <summary>A SimVar's FLOAT64 as the parameter's canonical int (spec §5.1, §7.3).</summary>
public static class ValueConverter
{
    /// <returns>
    /// The canonical value, or null when the sim has no real value. A frequency of exactly
    /// 0 Hz is the case seen: COM 1 read 0.000 / 0.000 mid-load in the TriStar (spec §5.3), and
    /// must never be shown, confirmed or stepped from. NaN and values outside an int are too.
    /// </returns>
    public static int? ToCanonical(double raw, ReadBinding read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (raw == 0 && IsFrequency(read.Unit))
        {
            return null;
        }

        // Math.Round, not a cast: 121.5 MHz × 1000 is 121499.99999999999 as a double.
        double scaled = Math.Round(raw * read.Scale, MidpointRounding.AwayFromZero);
        return double.IsFinite(scaled) && scaled >= int.MinValue && scaled <= int.MaxValue ? (int)scaled : null;
    }

    private static bool IsFrequency(string unit) =>
        unit.Equals("Hz", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("kHz", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("MHz", StringComparison.OrdinalIgnoreCase);
}
