using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Panels;
using Crowsnest.Device;
using Crowsnest.Device.Simulation;
using Crowsnest.Sim;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crowsnest.Host.Tests;

/// <summary>
/// The bridge end to end (spec §6.2, §8): simulated boards on pretend USB, the real
/// coordinator and panels, the fake sim, and a real settings file in a folder of its own.
/// </summary>
public sealed class BridgeHostedServiceTests : IAsyncDisposable
{
    private const string A = "a4cb8fdccc6c";
    private const string B = "a4cb8fdc1234";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "crowsnest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SimulatedUsb _usb = new();
    private readonly PanelSetup _setup = PanelCatalog.Load();
    private SettingsStore? _settings;
    private BridgeHostedService? _bridge;

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, json);
    }

    private async Task StartAsync()
    {
        _settings = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsPath);
        SettingsStore settings = _settings;
        DeviceManager devices = new(
            new DeviceManagerOptions
            {
                ScanInterval = TimeSpan.FromMilliseconds(30),
                FindPorts = _usb.FindPorts,
                Open = _usb.Open,
                Connection = new DeviceConnectionOptions { BrightnessFor = identity => settings.Current.Find(identity.HardwareId)?.Brightness },
            },
            NullLogger<DeviceManager>.Instance);

        _bridge = new BridgeHostedService(
            _setup, _settings, devices, new FakeParameterGateway(_setup), DefaultInputActionMap.Instance, TimeProvider.System,
            NullLogger<BridgeHostedService>.Instance);
        await _bridge.StartAsync(CancellationToken.None);
    }

    private SimulatedDevice? On(string port) => _usb.Device(port);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.StopAsync(CancellationToken.None);
            _bridge.Dispose();
        }

        await _usb.DisposeAsync();
        _settings?.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnlistedDeviceShowsNotAssignedThenWhatTheSettingsGiveIt()
    {
        _usb.Plug("COM7", A);
        await StartAsync();

        await Eventually(() => On("COM7")?.Latest?.Page.Id == PanelEngine.UnassignedPageId, "the unassigned screen");
        Assert.Equal("dccc6c", On("COM7")!.Latest!.Fields[0].Text);

        File.WriteAllText(SettingsPath, """{ "devices": { "dccc6c": { "panels": [ "nav" ], "brightness": 30 } } }""");

        await Eventually(() => On("COM7")?.Latest?.Page.Id == "nav1", "NAV 1 on the device");
        await Eventually(() => On("COM7")!.Brightness == 30, "the new brightness");
        Assert.Equal(1, _usb.Opens("COM7")); // applied over the open link, no reconnect
    }

    [Fact]
    public async Task ListedDevicesStartOnTheirOwnPanelsAndBrightness()
    {
        WriteSettings($$"""{ "devices": { "{{A}}": { "panels": [ "com" ], "brightness": 40 }, "dc1234": { "panels": [ "nav" ] } } }""");
        _usb.Plug("COM7", A);
        _usb.Plug("COM8", B);

        await StartAsync();

        await Eventually(() => On("COM7")?.Latest?.Page.Id == "com1" && On("COM8")?.Latest?.Page.Id == "nav1", "both devices' first pages");
        Assert.Equal(2, On("COM7")!.Latest!.Page.Count);
        Assert.Equal(1, On("COM8")!.Latest!.Page.Count);
        Assert.Equal(40, On("COM7")!.Brightness);
        Assert.Equal(80, On("COM8")!.Brightness); // the default
    }

    [Fact]
    public async Task ADeviceThatIsUnpluggedAndPluggedBackInGetsItsPanelsAgain()
    {
        WriteSettings("""{ "devices": { "dccc6c": { "panels": [ "nav" ] } } }""");
        _usb.Plug("COM7", A);
        await StartAsync();
        await Eventually(() => On("COM7")?.Latest?.Page.Id == "nav1", "NAV 1");

        await _usb.UnplugAsync("COM7");
        await Task.Delay(100);
        _usb.Plug("COM7", A);

        await Eventually(() => _usb.Opens("COM7") == 2 && On("COM7")?.Latest?.Page.Id == "nav1", "NAV 1 after the replug");
    }

}
