using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;

namespace Crowsnest.Host.Tests;

public sealed class HealthSnapshotTests
{
    private static DeviceHealth Device(string hardwareId, string? name = null, params string[] panels) =>
        new(new DeviceIdentity(hardwareId, "crowpanel-1.28", "0.3.4"), "COM7", name ?? DeviceIdentity.ShortIdOf(hardwareId), panels);

    [Theory]
    [InlineData(SimConnectionState.Disconnected, 0, HealthLight.Grey)]
    [InlineData(SimConnectionState.Connecting, 0, HealthLight.Grey)]
    [InlineData(SimConnectionState.Connected, 0, HealthLight.Amber)]
    [InlineData(SimConnectionState.Faulted, 1, HealthLight.Amber)]
    [InlineData(SimConnectionState.Connected, 2, HealthLight.Green)]
    public void TheLightIsGreenOnlyWithBothSimAndADevice(SimConnectionState sim, int devices, HealthLight expected)
    {
        HealthSnapshot health = new(sim, [.. Enumerable.Range(0, devices).Select(i => Device($"a4cb8fdc000{i}"))], []);

        Assert.Equal(expected, health.Light);
    }

    [Fact]
    public void DevicesAreDescribedByNameShortIdAndPanels()
    {
        HealthSnapshot health = new(SimConnectionState.Connected, [Device("a4cb8fdccc6c", "Radios", "com", "nav"), Device("a4cb8fdc1234")], []);

        Assert.Equal(["Radios (dccc6c): com, nav", "dc1234: not assigned"], health.DeviceLines);
        Assert.Equal("Crowsnest\nMSFS: connected\n2 devices, 1 not assigned", health.ToolTip);
    }

    [Fact]
    public void WithNothingConnectedTheTooltipSaysSo()
    {
        HealthSnapshot health = new(SimConnectionState.Disconnected, [], []);

        Assert.Equal(["No devices connected"], health.DeviceLines);
        Assert.Equal("Crowsnest\nMSFS: not running\nNo devices connected", health.ToolTip);
    }

    [Fact]
    public void ALongTooltipIsCutToWhatWindowsShows()
    {
        HealthSnapshot health = new(SimConnectionState.Connected, [Device("a4cb8fdccc6c", new string('x', 200), "com")], []);

        Assert.Equal(127, health.ToolTip.Length);
    }

    [Fact]
    public void PortsThatCouldNotBeUsedAreListedAndNamedInTheTooltip()
    {
        HealthSnapshot one = new(SimConnectionState.Connected, [], [new UnavailablePort("COM5", InUse: true, "Access to the path 'COM5' is denied.")]);
        HealthSnapshot two = one with { Ports = [.. one.Ports, new UnavailablePort("COM9", InUse: false, "No hello frame within 500 ms.")] };

        Assert.Equal(["COM5: in use by another program"], one.PortLines);
        Assert.Equal("Crowsnest\nMSFS: connected\nNo devices connected\nCOM5: in use by another program", one.ToolTip);
        Assert.Equal(["COM5: in use by another program", "COM9: not answering"], two.PortLines);
        Assert.EndsWith("\n2 ports unavailable", two.ToolTip, StringComparison.Ordinal);
        Assert.Empty(new HealthSnapshot(SimConnectionState.Connected, [], []).PortLines);
    }
}
