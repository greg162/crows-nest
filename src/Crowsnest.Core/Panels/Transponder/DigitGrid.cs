using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Panels.Transponder;

/// <summary>
/// A code of independent digits in a small radix (spec §5.3): the transponder's four octal
/// digits, 0000-7777.
///
/// The value is the code as it reads, one digit per decimal place, so squawk 7700 is the int
/// 7700, not 0o7700. That keeps the registry, the logs and the display in the same numbers
/// the pilot sees, and BCO16 is the same digits packed into nibbles. A cursor picks a digit by
/// its place value (1000, 100, 10 or 1). <see cref="CursorWrap.WrapWithinParent"/> turns that
/// digit alone, 7 → 0, the way a transponder knob does; <see cref="CursorWrap.Carry"/> counts
/// in the radix, 0077 → 0100, and stops at the ends; <see cref="CursorWrap.Clamp"/> stops the
/// digit at 0 and 7.
///
/// Lives in the transponder module rather than <c>Domain/Grids/</c> because only the
/// transponder uses it (spec §4.1).
/// </summary>
public sealed class DigitGrid : IValueGrid
{
    private readonly int[] _codes;

    public DigitGrid(int digits = 4, int radix = 8)
    {
        if (digits is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "Between 1 and 9 digits.");
        }

        if (radix is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(radix), radix, "The radix must fit in one decimal digit.");
        }

        Digits = digits;
        Radix = radix;

        // Counting 0 .. radix^digits - 1 and writing each count out digit by digit gives the
        // codes in ascending order, which Snap's binary search relies on.
        _codes = new int[(int)Math.Pow(radix, digits)];
        for (int count = 0; count < _codes.Length; count++)
        {
            _codes[count] = ToCode(count);
        }
    }

    public int Digits { get; }

    public int Radix { get; }

    public int Min => 0;

    public int Max => _codes[^1];

    public IReadOnlyList<int> Codes => _codes;

    public bool Contains(int value) => Array.BinarySearch(_codes, value) >= 0;

    /// <summary>
    /// Nearest code by value, so 7790 snaps to 7777 and 1238 to 1237. An exact tie snaps down.
    /// Only reached when a sim reports something that is not a code.
    /// </summary>
    public int Snap(int value) => _codes[NearestIndex(value)];

    public int Step(int from, int detents, CursorLevel cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        int place = PlaceOf(cursor);
        int index = NearestIndex(from);

        if (cursor.Wrap == CursorWrap.Carry)
        {
            long count = index + detents * (long)Math.Pow(Radix, place);
            return _codes[(int)Math.Clamp(count, 0, _codes.Length - 1)];
        }

        int code = _codes[index];
        int placeValue = (int)Math.Pow(10, place);
        int digit = code / placeValue % 10;
        int turned = cursor.Wrap == CursorWrap.WrapWithinParent
            ? (int)((digit + (long)detents) % Radix + Radix) % Radix
            : (int)Math.Clamp(digit + (long)detents, 0, Radix - 1);

        return code + (turned - digit) * placeValue;
    }

    private int PlaceOf(CursorLevel cursor)
    {
        int placeValue = 1;
        for (int place = 0; place < Digits; place++, placeValue *= 10)
        {
            if (cursor.Step == placeValue)
            {
                return place;
            }
        }

        throw new ArgumentException($"Cursor '{cursor.Name}' steps by {cursor.Step}, which is not the place value of one of the {Digits} digits.", nameof(cursor));
    }

    private int ToCode(int count)
    {
        int code = 0;
        for (int placeValue = 1; count > 0; placeValue *= 10, count /= Radix)
        {
            code += count % Radix * placeValue;
        }

        return code;
    }

    private int NearestIndex(int value)
    {
        int index = Array.BinarySearch(_codes, value);
        if (index >= 0)
        {
            return index;
        }

        int above = ~index;
        if (above == 0)
        {
            return 0;
        }

        if (above == _codes.Length)
        {
            return _codes.Length - 1;
        }

        int below = above - 1;
        return value - _codes[below] <= _codes[above] - value ? below : above;
    }
}
