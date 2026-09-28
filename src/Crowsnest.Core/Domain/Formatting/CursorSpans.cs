namespace Crowsnest.Core.Domain.Formatting;

/// <summary>
/// Resolves a <see cref="CursorLevel.DisplaySpan"/> against the text it underlines (spec §5.2).
///
/// A span may count from the start (<c>4..7</c>, the kHz of <c>"121.500"</c>) or from the end
/// (<c>..^4</c>, the thousands of <c>"12,000"</c> or <c>"9,000"</c>). The end form is what
/// makes a variable-width value work with a span fixed in the registry. The device only ever
/// sees the resolved form: start and end counted from the start.
/// </summary>
public static class CursorSpans
{
    /// <summary>The span as start-relative indices into a string of <paramref name="textLength"/> characters.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The span does not fit, or is empty.</exception>
    public static Range Resolve(Range span, int textLength)
    {
        if (!TryResolve(span, textLength, out Range resolved))
        {
            throw new ArgumentOutOfRangeException(nameof(span), span, $"The span does not fit in {textLength} characters.");
        }

        return resolved;
    }

    /// <summary>
    /// Reads a span as the registry writes it, in C# range syntax: <c>"4..7"</c>, <c>"..^4"</c>,
    /// <c>"^3..^2"</c>. An omitted start is 0 and an omitted end is <c>^0</c>.
    /// </summary>
    public static bool TryParse(string? text, out Range span)
    {
        span = default;
        if (text is null)
        {
            return false;
        }

        int dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0 || text.IndexOf("..", dots + 2, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (TryParseIndex(text[..dots], Index.Start, out Index start) &&
            TryParseIndex(text[(dots + 2)..], Index.End, out Index end))
        {
            span = start..end;
            return true;
        }

        return false;
    }

    private static bool TryParseIndex(string text, Index omitted, out Index index)
    {
        index = omitted;
        if (text.Length == 0)
        {
            return true;
        }

        bool fromEnd = text[0] == '^';
        string digits = fromEnd ? text[1..] : text;

        // Digits only: no sign, no whitespace, nothing int.TryParse would otherwise let through.
        if (digits.Length == 0 || digits.Length > 4 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        index = new Index(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), fromEnd);
        return true;
    }

    public static bool TryResolve(Range span, int textLength, out Range resolved)
    {
        int start = span.Start.GetOffset(textLength);
        int end = span.End.GetOffset(textLength);

        if (start < 0 || end > textLength || start >= end)
        {
            resolved = default;
            return false;
        }

        resolved = start..end;
        return true;
    }
}
