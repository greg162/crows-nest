namespace Crowsnest.Core.Domain.Grids;

/// <summary>
/// The set of legal values a parameter may take, in its canonical unit (spec §5.3).
/// Implementations are pure and immutable.
/// </summary>
public interface IValueGrid
{
    bool Contains(int value);

    /// <summary>Nearest legal value. Values outside the range snap to its ends.</summary>
    int Snap(int value);

    /// <summary>
    /// The value <paramref name="detents"/> clicks away from <paramref name="from"/> at the
    /// given cursor level. An off-grid <paramref name="from"/> is snapped first.
    /// </summary>
    int Step(int from, int detents, CursorLevel cursor);
}
