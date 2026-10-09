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

        if (IsPackedDigits(read.Unit))
        {
            return Unpack(raw);
        }

        // Math.Round, not a cast: 121.5 MHz × 1000 is 121499.99999999999 as a double.
        double scaled = Math.Round(raw * read.Scale, MidpointRounding.AwayFromZero);
        return double.IsFinite(scaled) && scaled >= int.MinValue && scaled <= int.MaxValue ? (int)scaled : null;
    }

    /// <summary>
    /// One decimal digit per nibble, as the sim keeps the transponder code: 0x7700 is squawk
    /// 7700 (spec §5.3). The inverse of <see cref="PayloadEncoder"/>'s BCD16. Null if a nibble
    /// is not a digit, which would mean the sim did not send packed digits at all.
    /// </summary>
    private static int? Unpack(double raw)
    {
        if (!double.IsFinite(raw) || raw < 0 || raw > 0xFFFF || raw != Math.Floor(raw))
        {
            return null;
        }

        int code = 0;
        int placeValue = 1;
        for (int packed = (int)raw; packed > 0; packed >>= 4, placeValue *= 10)
        {
            int digit = packed & 0xF;
            if (digit > 9)
            {
                return null;
            }

            code += digit * placeValue;
        }

        return code;
    }

    private static bool IsPackedDigits(string unit) =>
        unit.Equals("BCO16", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("BCD16", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrequency(string unit) =>
        unit.Equals("Hz", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("kHz", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("MHz", StringComparison.OrdinalIgnoreCase);
}
