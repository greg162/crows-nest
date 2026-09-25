using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Panels.Com;

/// <summary>
/// Legal COM channels in kHz, by ICAO channel name (spec §5.3).
///
/// Under 8.33 kHz spacing each 25 kHz slot holds four names: the slot's own 25 kHz name
/// and three 8.33 kHz names 5, 10 and 15 kHz above it. So within every 100 kHz block the
/// legal fractions are .000 .005 .010 .015 .025 ... .090, and .020 .045 .070 .095 do not
/// exist. Stepping is index arithmetic over a precomputed table, never <c>value + n</c>,
/// because the gaps make the distance between neighbours uneven.
/// </summary>
public sealed class ComChannelGrid : IValueGrid
{
    public const int BandMinKhz = 118_000;
    public const int BandMaxKhz = 136_990;

    private const int SlotKhz = 25;
    private const int KhzPerMhz = 1000;

    private readonly int[] _channels;

    public ComChannelGrid(ChannelSpacing spacing, int minKhz = BandMinKhz, int maxKhz = BandMaxKhz)
    {
        Spacing = spacing;

        List<int> channels = [];
        for (int khz = minKhz; khz <= maxKhz; khz++)
        {
            if (IsChannelName(khz, spacing))
            {
                channels.Add(khz);
            }
        }

        if (channels.Count == 0)
        {
            throw new ArgumentException($"No {spacing} channels between {minKhz} and {maxKhz} kHz.", nameof(minKhz));
        }

        _channels = [.. channels];
    }

    public ChannelSpacing Spacing { get; }

    public IReadOnlyList<int> Channels => _channels;

    public int Min => _channels[0];

    public int Max => _channels[^1];

    public bool Contains(int value) => Array.BinarySearch(_channels, value) >= 0;

    /// <summary>Nearest channel name. An exact tie between two names snaps down.</summary>
    public int Snap(int value) => _channels[NearestIndex(value)];

    public int Step(int from, int detents, CursorLevel cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        int index = NearestIndex(from);
        return cursor.Step % KhzPerMhz == 0
            ? StepMegahertz(_channels[index], detents * (cursor.Step / KhzPerMhz), cursor.Wrap)
            : StepChannels(index, detents * cursor.Step, cursor.Wrap);
    }

    private static bool IsChannelName(int khz, ChannelSpacing spacing)
    {
        int offset = khz % SlotKhz;
        return spacing == ChannelSpacing.TwentyFiveKhz
            ? offset == 0
            : offset is 0 or 5 or 10 or 15;
    }

    private int StepChannels(int index, int channels, CursorWrap wrap)
    {
        if (wrap != CursorWrap.WrapWithinParent)
        {
            return _channels[Math.Clamp(index + channels, 0, _channels.Length - 1)];
        }

        // The parent is the MHz. The table is sorted, so its channels are contiguous.
        int mhz = _channels[index] / KhzPerMhz;
        int first = FirstIndexAtOrAbove(mhz * KhzPerMhz);
        int count = FirstIndexAtOrAbove((mhz + 1) * KhzPerMhz) - first;

        return _channels[first + Mod(index - first + channels, count)];
    }

    private int StepMegahertz(int from, int megahertz, CursorWrap wrap)
    {
        int minMhz = Min / KhzPerMhz;
        int maxMhz = Max / KhzPerMhz;
        int mhz = from / KhzPerMhz + megahertz;

        mhz = wrap == CursorWrap.WrapWithinParent
            ? minMhz + Mod(mhz - minMhz, maxMhz - minMhz + 1)
            : Math.Clamp(mhz, minMhz, maxMhz);

        // The fraction survives the move. It is only not a channel when the band edge cuts
        // a MHz short, and then the nearest channel is the honest answer.
        return Snap(mhz * KhzPerMhz + from % KhzPerMhz);
    }

    private int NearestIndex(int value)
    {
        int index = Array.BinarySearch(_channels, value);
        if (index >= 0)
        {
            return index;
        }

        int above = ~index;
        if (above == 0)
        {
            return 0;
        }

        if (above == _channels.Length)
        {
            return _channels.Length - 1;
        }

        int below = above - 1;
        return value - _channels[below] <= _channels[above] - value ? below : above;
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private int FirstIndexAtOrAbove(int value)
    {
        int index = Array.BinarySearch(_channels, value);
        return index >= 0 ? index : ~index;
    }
}
