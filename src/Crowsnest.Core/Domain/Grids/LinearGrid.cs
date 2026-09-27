namespace Crowsnest.Core.Domain.Grids;

/// <summary>
/// Evenly spaced values from <see cref="Min"/> in steps of <see cref="StepSize"/> (spec §5.3):
/// NAV at 50 kHz, autopilot altitude at 100 ft, airspeed at 1 kt, the barometer.
///
/// <see cref="CursorLevel.Step"/> is read in canonical units and must be a multiple of the
/// grid's step. <paramref name="parent"/> in the constructor is the digit group above the fine
/// cursors, e.g. 1000 for the MHz of a NAV frequency. A cursor whose step is a multiple of it
/// moves the parent and keeps the fraction, as <c>ComChannelGrid</c> does with MHz; a finer
/// cursor that wraps goes round inside the parent: 108.950 → 108.000. With no parent, a
/// wrapping cursor goes round the whole range.
/// </summary>
public sealed class LinearGrid : IValueGrid
{
    private readonly int? _parent;

    public LinearGrid(int min, int max, int step, int? parent = null)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be positive.");
        }

        if (max < min)
        {
            throw new ArgumentException($"No values between {min} and {max}.", nameof(max));
        }

        if (parent is { } p && (p <= step || p % step != 0))
        {
            throw new ArgumentException($"The parent {p} must be a larger multiple of the step {step}.", nameof(parent));
        }

        Min = min;
        Max = min + (max - min) / step * step;
        StepSize = step;
        _parent = parent;
    }

    public int Min { get; }

    /// <summary>The last value on the grid, which is <c>max</c> rounded down onto it.</summary>
    public int Max { get; }

    public int StepSize { get; }

    public int Count => (Max - Min) / StepSize + 1;

    public bool Contains(int value) => value >= Min && value <= Max && (value - Min) % StepSize == 0;

    /// <summary>Nearest value on the grid. An exact tie between two values snaps down.</summary>
    public int Snap(int value) => ValueAt(NearestIndex(value));

    public int Step(int from, int detents, CursorLevel cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursor.Step <= 0 || cursor.Step % StepSize != 0)
        {
            throw new ArgumentException($"Cursor '{cursor.Name}' steps by {cursor.Step}, which is not a multiple of the grid step {StepSize}.", nameof(cursor));
        }

        int index = NearestIndex(from);
        return _parent is { } parent && cursor.Step % parent == 0
            ? StepParent(ValueAt(index), detents * (cursor.Step / parent), parent, cursor.Wrap)
            : StepValues(index, detents * (cursor.Step / StepSize), cursor.Wrap);
    }

    private int StepValues(int index, int steps, CursorWrap wrap)
    {
        if (wrap != CursorWrap.WrapWithinParent)
        {
            return ValueAt(Math.Clamp(index + steps, 0, Count - 1));
        }

        if (_parent is not { } parent)
        {
            return ValueAt(Mod(index + steps, Count));
        }

        // The grid values inside this parent block, clipped to the range at either end.
        int block = FloorDiv(ValueAt(index), parent) * parent;
        int first = Math.Max(0, CeilDiv(block - Min, StepSize));
        int last = Math.Min(Count - 1, FloorDiv(block + parent - 1 - Min, StepSize));

        return ValueAt(first + Mod(index - first + steps, last - first + 1));
    }

    private int StepParent(int from, int parents, int parent, CursorWrap wrap)
    {
        int minParent = FloorDiv(Min, parent);
        int maxParent = FloorDiv(Max, parent);
        int target = FloorDiv(from, parent) + parents;

        target = wrap == CursorWrap.WrapWithinParent
            ? minParent + Mod(target - minParent, maxParent - minParent + 1)
            : Math.Clamp(target, minParent, maxParent);

        // The fraction survives the move. It only falls off the grid when a range edge cuts
        // a parent short, and then the nearest value is the honest answer.
        return Snap(target * parent + Mod(from, parent));
    }

    private int ValueAt(int index) => Min + index * StepSize;

    private int NearestIndex(int value)
    {
        if (value <= Min)
        {
            return 0;
        }

        if (value >= Max)
        {
            return Count - 1;
        }

        int below = (value - Min) / StepSize;
        int offset = (value - Min) % StepSize;
        return offset * 2 <= StepSize ? below : below + 1;
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    // Divisors are always positive. Values can be negative (vertical speed, a minimum below 0).
    private static int FloorDiv(int value, int divisor) => (value - Mod(value, divisor)) / divisor;

    private static int CeilDiv(int value, int divisor) => -FloorDiv(-value, divisor);
}
