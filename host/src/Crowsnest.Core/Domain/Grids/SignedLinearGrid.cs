namespace Crowsnest.Core.Domain.Grids;

/// <summary>
/// Evenly spaced values either side of zero (spec §5.3): autopilot vertical speed, e.g.
/// -8000 to +8000 fpm in 100 fpm steps.
///
/// Three things set it apart from <see cref="LinearGrid"/>. Zero is always on the grid, so
/// the pilot can always dial level flight. It never wraps: a cursor that went from +8000 to
/// -8000 would command a dive, so <see cref="CursorWrap.WrapWithinParent"/> is rejected and
/// every other mode stops at the ends. And an exact tie snaps toward zero rather than down,
/// so climb and descent behave alike: <c>Snap(-v) == -Snap(v)</c>.
/// </summary>
public sealed class SignedLinearGrid : IValueGrid
{
    public SignedLinearGrid(int min, int max, int step)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be positive.");
        }

        if (min >= 0 || max <= 0)
        {
            throw new ArgumentException($"{min}..{max} does not straddle zero.", nameof(min));
        }

        if (min % step != 0 || max % step != 0)
        {
            throw new ArgumentException($"{min} and {max} must both be multiples of the step {step}, so zero is on the grid.", nameof(step));
        }

        Min = min;
        Max = max;
        StepSize = step;
    }

    public int Min { get; }

    public int Max { get; }

    public int StepSize { get; }

    public bool Contains(int value) => value >= Min && value <= Max && value % StepSize == 0;

    /// <summary>Nearest value on the grid. An exact tie snaps toward zero.</summary>
    public int Snap(int value)
    {
        // long, because Math.Abs(int.MinValue) overflows.
        long magnitude = Math.Abs((long)value);
        long below = magnitude / StepSize * StepSize;
        long nearest = (magnitude - below) * 2 <= StepSize ? below : below + StepSize;
        return (int)Math.Clamp(value < 0 ? -nearest : nearest, Min, Max);
    }

    public int Step(int from, int detents, CursorLevel cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursor.Step <= 0 || cursor.Step % StepSize != 0)
        {
            throw new ArgumentException($"Cursor '{cursor.Name}' steps by {cursor.Step}, which is not a multiple of the grid step {StepSize}.", nameof(cursor));
        }

        if (cursor.Wrap == CursorWrap.WrapWithinParent)
        {
            throw new ArgumentException($"Cursor '{cursor.Name}' wraps, which would jump from climb to descent.", nameof(cursor));
        }

        long target = Snap(from) + (long)detents * cursor.Step;
        return (int)Math.Clamp(target, Min, Max);
    }
}
