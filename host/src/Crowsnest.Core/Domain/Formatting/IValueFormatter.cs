namespace Crowsnest.Core.Domain.Formatting;

/// <summary>
/// Turns a canonical value into the text the device shows (spec §5.6). The device never sees
/// the number, only this string and a cursor span into it.
///
/// Must not throw for any int: the value may be whatever the sim reported, on the grid or not.
/// </summary>
public interface IValueFormatter
{
    string Format(int value);

    /// <summary>Shown before the sim has reported a value: <c>"---.---"</c>.</summary>
    string Placeholder { get; }
}
