using System.Globalization;

namespace Crowsnest.Core.Domain.Formatting;

/// <summary>
/// A non-negative integer zero-padded to a fixed width: heading and course <c>"005"</c>
/// (registry key <c>deg3</c>), a squawk <c>"0077"</c> (<c>code4</c>). Fixed width means cursor
/// spans can count from the start: <c>0..2</c> is the tens of a heading.
/// </summary>
public sealed class PaddedFormatter : IValueFormatter
{
    private readonly string _format;

    public PaddedFormatter(int digits)
    {
        if (digits is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "Between 1 and 10 digits.");
        }

        _format = "D" + digits.ToString(CultureInfo.InvariantCulture);
        Placeholder = new string('-', digits);
    }

    public string Placeholder { get; }

    /// <summary>Negative values keep their sign rather than being hidden; they never come from a grid.</summary>
    public string Format(int value) => value.ToString(_format, CultureInfo.InvariantCulture);
}
