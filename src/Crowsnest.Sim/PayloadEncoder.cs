using Crowsnest.Core.Domain;

namespace Crowsnest.Sim;

/// <summary>A canonical value as the 32-bit key event payload its <see cref="PayloadEncoding"/> calls for (spec §7.3).</summary>
public static class PayloadEncoder
{
    /// <exception cref="ArgumentOutOfRangeException">The value cannot be expressed in the encoding.</exception>
    public static uint Encode(int value, PayloadEncoding encoding) => encoding switch
    {
        // kHz → Hz. 136,990 kHz is 136,990,000 Hz, well inside a uint.
        PayloadEncoding.Hz when value >= 0 => checked((uint)value * 1000u),
        PayloadEncoding.Raw when value >= 0 => (uint)value,

        // Vertical speed: the sim reads the payload back as a signed int.
        PayloadEncoding.Signed => unchecked((uint)value),

        PayloadEncoding.Bcd16 => Bcd16(value),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, $"{value} cannot be sent as {encoding}."),
    };

    /// <summary>
    /// Four decimal digits into four nibbles: squawk 7700 → 0x7700 (the transponder's BCO16,
    /// spec §5.3: the canonical value is the code as it reads, so this is digit packing, not
    /// a base conversion).
    /// </summary>
    private static uint Bcd16(int value)
    {
        if (value is < 0 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "BCD16 holds four decimal digits.");
        }

        uint packed = 0;
        for (int shift = 0; value > 0; shift += 4, value /= 10)
        {
            packed |= (uint)(value % 10) << shift;
        }

        return packed;
    }
}
