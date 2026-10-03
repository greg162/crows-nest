using System.Globalization;

namespace Crowsnest.Core.Domain.Formatting;

/// <summary>
/// A kHz value as MHz: <c>"121.500"</c> with three decimals (COM, registry key <c>freq3</c>),
/// <c>"108.05"</c> with two (NAV, <c>freq2</c>).
///
/// With two decimals the last kHz digit is dropped, not rounded, the way a two-decimal radio
/// display shows it. That is only lossless on a grid of 10 kHz multiples, so never use
/// <c>freq2</c> for COM: every 8.33 channel name would lose its third digit (spec §5.2).
/// </summary>
public sealed class FrequencyFormatter : IValueFormatter
{
    private const int KhzPerMhz = 1000;

    private readonly int _decimals;
    private readonly int _divisor;

    public FrequencyFormatter(int decimals)
    {
        if (decimals is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(decimals), decimals, "A kHz value has 1 to 3 decimal places of MHz.");
        }

        _decimals = decimals;
        _divisor = (int)Math.Pow(10, 3 - decimals);
        Placeholder = "---." + new string('-', decimals);
    }

    public string Placeholder { get; }

    public string Format(int value)
    {
        // long, so Math.Abs(int.MinValue) cannot overflow. Frequencies are never negative, but
        // the formatter must not throw on whatever the sim sends.
        long khz = Math.Abs((long)value);
        string sign = value < 0 ? "-" : "";
        long mhz = khz / KhzPerMhz;
        long fraction = khz % KhzPerMhz / _divisor;

        return string.Create(CultureInfo.InvariantCulture, $"{sign}{mhz}.{fraction.ToString("D" + _decimals, CultureInfo.InvariantCulture)}");
    }
}
