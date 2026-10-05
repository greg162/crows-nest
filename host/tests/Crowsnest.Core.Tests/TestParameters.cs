using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;
using Crowsnest.Core.Domain.Grids;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests;

/// <summary>Parameters built by hand, for tests of the parts below the registry.</summary>
internal static class TestParameters
{
    public static readonly CursorLevel Mhz = new("mhz", 1000, CursorWrap.Clamp, 0..3);
    public static readonly CursorLevel Khz = new("khz", 1, CursorWrap.WrapWithinParent, 4..7);

    /// <summary>COM 1 standby on a 25 kHz grid, shaped like the registry's entry.</summary>
    public static ParameterDefinition Com1Standby(IReadOnlyList<CursorLevel>? cursors = null, IValueGrid? grid = null) => new(
        new ParameterId("com1.standby"),
        "COM 1 STBY",
        CanonicalUnit.Kilohertz,
        grid ?? new ComChannelGrid(ChannelSpacing.TwentyFiveKhz),
        cursors ?? [Mhz, Khz],
        new ReadBinding(ReadSource.SimVar, "COM STANDBY FREQUENCY:1", "Hz", 0.001),
        new WriteBinding(WriteMode.KeyEvent, "COM_STBY_RADIO_SET_HZ", PayloadEncoding.Hz),
        new FrequencyFormatter(decimals: 3));
}
