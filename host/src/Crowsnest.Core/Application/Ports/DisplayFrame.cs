namespace Crowsnest.Core.Application.Ports;

/// <summary>
/// One complete screen, expressed as pre-formatted text (spec §5.6).
///
/// This is the pivotal type for extensibility: the device receives "12,000" with a
/// cursor span, not an altitude in feet. It has no concept of frequencies, headings
/// or units, which is why adding the entire autopilot costs zero firmware changes.
/// </summary>
/// <param name="Revision">Monotonic. The device discards any frame older than the last it applied.</param>
/// <param name="AckSequence">Highest device input sequence the host has processed.</param>
public sealed record DisplayFrame(
    long Revision,
    SimConnectionState Sim,
    PageDescriptor Page,
    IReadOnlyList<FieldDescriptor> Fields,
    string? Notice = null,
    long AckSequence = 0);

public sealed record PageDescriptor(
    string Id,
    string Title,
    PageLayout Layout,
    int Index,
    int Count);

/// <param name="CursorSpan">
/// Character range into <paramref name="Text"/> that the device underlines, [start, end).
/// The host computes it from the parameter's cursor level; the device just underlines.
/// </param>
/// <param name="Pending">True while a written value is awaiting the sim's confirmation.</param>
public sealed record FieldDescriptor(
    FieldRole Role,
    string Label,
    string Text,
    Range? CursorSpan = null,
    bool Pending = false);

public enum FieldRole
{
    Primary,
    Secondary,
    Tertiary,
}
