namespace Crowsnest.Core.Domain;

/// <summary>
/// One level of the edit cursor: which digit group a detent moves, and by how much
/// (spec §5.2). A parameter's cursors are ordered coarse to fine.
/// </summary>
/// <param name="Step">
/// Distance per detent. Its unit is the grid's to interpret: a <c>LinearGrid</c> reads it
/// in canonical units, a <c>ComChannelGrid</c> reads a whole-MHz multiple as MHz and
/// anything else as a count of channels.
/// </param>
/// <param name="DisplaySpan">Characters of the formatted value this cursor underlines, [start, end).</param>
public sealed record CursorLevel(string Name, int Step, CursorWrap Wrap, Range DisplaySpan);

public enum CursorWrap
{
    /// <summary>Wrap inside the parent digit group: 118.990 → 118.000, the way a real radio does.</summary>
    WrapWithinParent,

    /// <summary>Carry into the parent digit group: 118.990 → 119.000. Stops at the ends of the range.</summary>
    Carry,

    /// <summary>Stop at the ends of the range.</summary>
    Clamp,
}
