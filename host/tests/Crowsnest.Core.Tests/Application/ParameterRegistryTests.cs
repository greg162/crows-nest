using Crowsnest.Core.Application;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;
using Crowsnest.Core.Panels;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Tests.Application;

/// <summary>The spec §11 registry row: every shipped entry loads and holds together.</summary>
public class ParameterRegistryTests
{
    private static readonly ParameterRegistry Registry = PanelCatalog.Load().Registry;

    public static TheoryData<string> ShippedIds => [.. Registry.All.Select(p => p.Id.Key)];

    [Fact]
    public void TheDefaultRegistryHasBothComsAndBothNavs()
    {
        Assert.Equal(
            ["com1.standby", "com1.active", "com2.standby", "com2.active", "nav1.standby", "nav1.active", "nav2.standby", "nav2.active"],
            Registry.All.Select(p => p.Id.Key));
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EveryCursorSpanFitsTheWidestValue(string id)
    {
        ParameterDefinition parameter = Registry[new ParameterId(id)];

        // The widest text is at one end of the range. A span over a variable-width value (..^4)
        // need not fit the narrowest ("500" has no thousands), but must fit the widest.
        string widest = new[] { parameter.Grid.Snap(int.MinValue), parameter.Grid.Snap(int.MaxValue) }
            .Select(parameter.Formatter.Format)
            .MaxBy(text => text.Length)!;

        Assert.All(parameter.Cursors, cursor =>
            Assert.True(CursorSpans.TryResolve(cursor.DisplaySpan, widest.Length, out _), $"cursor '{cursor.Name}' span {cursor.DisplaySpan} does not fit \"{widest}\""));
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EveryCursorStepsFromEitherEndOntoTheGrid(string id)
    {
        ParameterDefinition parameter = Registry[new ParameterId(id)];
        int[] ends = [parameter.Grid.Snap(int.MinValue), parameter.Grid.Snap(int.MaxValue)];

        Assert.All(parameter.Cursors, cursor => Assert.All(ends, end =>
        {
            Assert.True(parameter.Grid.Contains(parameter.Grid.Step(end, 1, cursor)));
            Assert.True(parameter.Grid.Contains(parameter.Grid.Step(end, -1, cursor)));
        }));
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EveryPlaceholderIsAsWideAsAValue(string id)
    {
        ParameterDefinition parameter = Registry[new ParameterId(id)];
        string value = parameter.Formatter.Format(parameter.Grid.Snap(int.MaxValue));

        Assert.Equal(value.Length, parameter.Formatter.Placeholder.Length);
    }

    [Fact]
    public void ComOneStandbyMatchesWhatTheSpikeVerified()
    {
        ParameterDefinition standby = Registry[new ParameterId("com1.standby")];

        Assert.Equal("COM 1 STBY", standby.Label);
        Assert.Equal(CanonicalUnit.Kilohertz, standby.Unit);
        Assert.IsType<ComChannelGrid>(standby.Grid);
        Assert.Equal(ChannelSpacing.TwentyFiveKhz, ((ComChannelGrid)standby.Grid).Spacing); // the safe default until the SimVar says otherwise
        Assert.Equal(new ReadBinding(ReadSource.SimVar, "COM STANDBY FREQUENCY:1", "Hz", 0.001), standby.Read);
        Assert.Equal(new WriteBinding(WriteMode.KeyEvent, "COM_STBY_RADIO_SET_HZ", PayloadEncoding.Hz), standby.Write);
        Assert.Equal("121.500", standby.Formatter.Format(121_500));
        Assert.Equal(["mhz", "khz"], standby.Cursors.Select(c => c.Name));
    }

    [Fact]
    public void ComOneActiveUsesTheUnnumberedEvent()
    {
        // Spec §7.3: there is no COM1_RADIO_SET_HZ.
        Assert.Equal("COM_RADIO_SET_HZ", Registry[new ParameterId("com1.active")].Write.Target);
    }

    [Fact]
    public void NavOneIsAFiftyKhzLinearGridShownToTwoDecimals()
    {
        ParameterDefinition standby = Registry[new ParameterId("nav1.standby")];

        Assert.True(standby.Grid.Contains(110_350));
        Assert.False(standby.Grid.Contains(110_325));
        Assert.Equal(108_000, standby.Grid.Step(108_950, 1, standby.Cursors[1])); // wraps within the MHz
        Assert.Equal("110.35", standby.Formatter.Format(110_350));
    }

    [Fact]
    public void TheGatewaySubscribesToEveryParameterAndTheSpacingWatches()
    {
        Assert.Equal(
            ["com1.standby", "com1.active", "com2.standby", "com2.active", "nav1.standby", "nav1.active", "nav2.standby", "nav2.active", "com1.spacing", "com2.spacing"],
            Registry.Subscriptions.Select(s => s.Id.Key));
        Assert.Equal(Registry.Subscriptions.Count, Registry.Subscriptions.Select(s => s.Read.Name).Distinct().Count());
        Assert.Equal(
            [new ReadBinding(ReadSource.SimVar, "COM SPACING MODE:1", "Enum", 1), new ReadBinding(ReadSource.SimVar, "COM SPACING MODE:2", "Enum", 1)],
            Registry.Watches.Select(w => w.Read));
    }

    [Fact]
    public void AnUnknownIdIsReportedByName()
    {
        KeyNotFoundException e = Assert.Throws<KeyNotFoundException>(() => Registry[new ParameterId("com9.standby")]);

        Assert.Contains("com9.standby", e.Message, StringComparison.Ordinal);
        Assert.False(Registry.TryGet(new ParameterId("com9.standby"), out _));
    }

    [Fact]
    public void AnIdDefinedTwiceIsRejected()
    {
        ParameterDefinition com = TestParameters.Com1Standby();

        Assert.Throws<InvalidDataException>(() => new ParameterRegistry([com, com]));
        Assert.Throws<InvalidDataException>(() => new ParameterRegistry([com], [new SimSubscription(com.Id, com.Read)]));
    }
}
