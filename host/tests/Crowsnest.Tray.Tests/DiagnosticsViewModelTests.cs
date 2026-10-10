using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;
using Crowsnest.Host;
using Crowsnest.Tray.Diagnostics;
using Serilog.Events;
using Serilog.Parsing;

namespace Crowsnest.Tray.Tests;

public class DiagnosticsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 21, 30, 0, TimeSpan.FromHours(10));

    private static DeviceHealth Device(string name, string? panel, LinkStats? link) =>
        new(new DeviceIdentity("a4cb8fdccc6c", "crowpanel-2.1-rotary", "0.4.1"), "COM4", name, panel, link);

    private static HealthSnapshot Health(params DeviceHealth[] devices) =>
        new(SimConnectionState.Connected, devices, [new UnavailablePort("COM5", InUse: true, "Access is denied")]);

    [Fact]
    public void EachDeviceIsOneRowOfText()
    {
        DiagnosticsViewModel model = new("1.0.0");
        LinkStats link = new(Now - TimeSpan.FromMinutes(12.5), FramesIn: 340, FramesOut: 512, FramesIgnored: 1, Restarts: 2, TimeSpan.FromMilliseconds(1.84));

        model.Refresh(Health(Device("Left seat", "com", link)), Now);

        DeviceDiagnostics row = Assert.Single(model.Devices);
        Assert.Equal(new DeviceDiagnostics("Left seat (dccc6c)", "COM4", "COM", "0.4.1", "12 min 30 s", "1.8 ms", 340, 512, 1, 2), row);
        Assert.Equal("MSFS: connected", model.SimLine);
        Assert.Equal(["COM5: in use by another program"], model.Ports);
        Assert.True(model.HasDevices);
        Assert.True(model.HasPorts);
    }

    [Fact]
    public void ADeviceNamedByItsIdAndUnassignedSaysSo()
    {
        DiagnosticsViewModel model = new("1.0.0");

        model.Refresh(Health(Device("dccc6c", null, null)), Now);

        DeviceDiagnostics row = Assert.Single(model.Devices);
        Assert.Equal(("dccc6c", "not assigned", "–", "–"), (row.Title, row.Panel, row.Connected, row.RoundTrip));
    }

    [Fact]
    public void NoDevicesIsSaidRatherThanAnEmptyTable()
    {
        DiagnosticsViewModel model = new("1.0.0");

        model.Refresh(new HealthSnapshot(SimConnectionState.Disconnected, [], []), Now);

        Assert.True(model.HasNoDevices);
        Assert.False(model.HasPorts);
    }

    [Fact]
    public void AnUnchangedRefreshRaisesNothing()
    {
        DiagnosticsViewModel model = new("1.0.0");
        LinkStats link = new(Now, 1, 2, 0, 0, null);
        model.Refresh(Health(Device("x", "com", link)), Now);
        List<string?> raised = [];
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Refresh(Health(Device("x", "com", link)), Now);

        Assert.Empty(raised);
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(723, "12 min 03 s")]
    [InlineData(7_500, "2 h 05 min")]
    [InlineData(-3, "0 s")]
    public void DurationsReadNaturally(int seconds, string expected) =>
        Assert.Equal(expected, DiagnosticsViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheReportHasTheScreenThenTheLog()
    {
        DiagnosticsViewModel model = new("1.2.3");
        model.Refresh(Health(Device("Left seat", "com", new LinkStats(Now, 3, 4, 0, 0, TimeSpan.FromMilliseconds(2)))), Now);

        string report = model.Report(["first line", "second line"]);

        Assert.StartsWith("Crowsnest 1.2.3, diagnostics at 2026-10-09 21:30:00 +10:00", report, StringComparison.Ordinal);
        Assert.Contains("Left seat (dccc6c) on COM4: panel COM, firmware 0.4.1", report, StringComparison.Ordinal);
        Assert.Contains("round trip 2.0 ms, frames in 3 / out 4", report, StringComparison.Ordinal);
        Assert.Contains("COM5: in use by another program", report, StringComparison.Ordinal);
        Assert.EndsWith($"Recent log (2 lines):{Environment.NewLine}first line{Environment.NewLine}second line{Environment.NewLine}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogTailKeepsOnlyTheNewestLines()
    {
        LogTail tail = new(capacity: 3);
        List<string> added = [];
        tail.LineAdded += added.Add;

        for (int i = 1; i <= 5; i++)
        {
            tail.Emit(Event($"line {i}"));
        }

        Assert.Equal(3, tail.Lines.Count);
        Assert.EndsWith("line 3", tail.Lines[0], StringComparison.Ordinal);
        Assert.EndsWith("line 5", tail.Lines[2], StringComparison.Ordinal);
        Assert.Equal(5, added.Count);
        Assert.Contains("[INF]", tail.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ALogListenerThatThrowsDoesNotStopTheLogging()
    {
        LogTail tail = new();
        tail.LineAdded += _ => throw new InvalidOperationException("boom");

        tail.Emit(Event("still kept"));

        Assert.EndsWith("still kept", Assert.Single(tail.Lines), StringComparison.Ordinal);
    }

    private static LogEvent Event(string text) =>
        new(Now, LogEventLevel.Information, null, new MessageTemplateParser().Parse(text), []);
}
