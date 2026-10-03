namespace Crowsnest.Core.Domain.Grids;

/// <summary>
/// Evenly spaced values on a circle (spec §5.3): heading and course, 0-359 in 1° steps, where
/// one step past <see cref="Max"/> is <see cref="Min"/> again.
///
/// Every cursor goes round the circle: 359 + 1 → 0, and 355 + 10 → 5. The circle is the top
/// digit group, so <see cref="CursorWrap.WrapWithinParent"/> and <see cref="CursorWrap.Carry"/>
/// mean the same thing here. <see cref="CursorWrap.Clamp"/> is rejected: a heading bug that
/// stops at 359 is a bug. Values outside the range are brought onto the circle before
/// snapping, so a sim that reports 360 or -10 reads as 0 or 350.
/// </summary>
public sealed class WrappingGrid : IValueGrid
{
    public WrappingGrid(int min, int max, int step)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be positive.");
        }

        if (max <= min || (max - min) % step != 0)
        {
            throw new ArgumentException($"{min}..{max} is not a whole number of {step} steps round a circle.", nameof(max));
        }

        Min = min;
        Max = max;
        StepSize = step;
    }

    public int Min { get; }

    public int Max { get; }

    public int StepSize { get; }

    public int Count => (Max - Min) / StepSize + 1;

    /// <summary>One full turn: 360 for a 0-359 heading.</summary>
    public int Period => Count * StepSize;

    public bool Contains(int value) => value >= Min && value <= Max && (value - Min) % StepSize == 0;

    /// <summary>
    /// Nearest value on the circle, so 359.6 rounds to 0 rather than stopping at 359. An
    /// exact tie between two values snaps down.
    /// </summary>
    public int Snap(int value)
    {
        int offset = Mod(value - Min, Period);
        int below = offset / StepSize;
        int index = (offset % StepSize) * 2 <= StepSize ? below : below + 1;
        return ValueAt(Mod(index, Count));
    }

    public int Step(int from, int detents, CursorLevel cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursor.Step <= 0 || cursor.Step % StepSize != 0)
        {
            throw new ArgumentException($"Cursor '{cursor.Name}' steps by {cursor.Step}, which is not a multiple of the grid step {StepSize}.", nameof(cursor));
        }

        if (cursor.Wrap == CursorWrap.Clamp)
        {
            throw new ArgumentException($"Cursor '{cursor.Name}' clamps, but a circle has no ends to stop at.", nameof(cursor));
        }

        int index = (Snap(from) - Min) / StepSize;
        long steps = (long)detents * (cursor.Step / StepSize);
        return ValueAt((int)(((index + steps) % Count + Count) % Count));
    }

    private int ValueAt(int index) => Min + index * StepSize;

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}
