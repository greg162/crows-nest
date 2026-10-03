using System.Globalization;

namespace Crowsnest.Core.Domain.Formatting;

/// <summary>
/// An integer with thousands separators: <c>"12,000"</c> ft (registry key <c>thousands</c>),
/// or signed, <c>"+1,500"</c> / <c>"-1,500"</c> / <c>"0"</c> fpm (<c>signedThousands</c>).
///
/// The width varies with the value, so cursor spans over it count from the end: <c>..^4</c>
/// is the thousands and up, <c>^3..^2</c> the hundreds digit (see <see cref="CursorSpans"/>).
/// Always the invariant culture: the device shows the same digits whatever Windows is set to.
/// </summary>
public sealed class GroupedFormatter : IValueFormatter
{
    private readonly bool _signed;

    public GroupedFormatter(bool signed = false)
    {
        _signed = signed;
    }

    public string Placeholder => "---";

    public string Format(int value)
    {
        string format = _signed ? "+#,##0;-#,##0;0" : "#,##0";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
