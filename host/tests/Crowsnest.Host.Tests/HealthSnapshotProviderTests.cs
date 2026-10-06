using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Panels;
using Crowsnest.Device;
using Crowsnest.Device.Simulation;
using Crowsnest.Sim;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crowsnest.Host.Tests;

/// <summary>What the tray sees (spec §8): the real device manager on pretend USB, the fake sim, a real settings file.</summary>
public sealed class HealthSnapshotProviderTests : IAsyncDisposable
{
    private const string A = "a4cb8fdccc6c";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "crowsnest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SimulatedUsb _usb = new();
    private readonly PanelSetup _setup = PanelCatalog.Load();
    private readonly CancellationTokenSource _stop = new();
    private readonly SettingsStore _settings;
    private readonly DeviceManager _devices;
    private readonly FakeParameterGateway _sim;
    private readonly HealthSnapshotProvider _health;
    private Task _serving = Task.CompletedTask;

    public HealthSnapshotProviderTests()
    {
        _settings = new SettingsStore(NullLogger<SettingsStore>.Instance, Path.Combine(_folder, "settings.json"));
        _devices = new DeviceManager(
            new DeviceManagerOptions { ScanInterval = TimeSpan.FromMilliseconds(30), FindPorts = _usb.FindPorts, Open = _usb.Open },
            NullLogger<DeviceManager>.Instance);
        _sim = new FakeParameterGateway(_setup);
        _health = new HealthSnapshotProvider(_setup, _settings, _devices, _sim, NullLogger<HealthSnapshotProvider>.Instance);
    }

    private void StartServing() =>
        _serving = _devices.RunAsync((_, ct) => Task.Delay(Timeout.Infinite, ct), _stop.Token);

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
        await _stop.CancelAsync();
        await _serving;
        _health.Dispose();
        await _sim.DisposeAsync();
        await _usb.DisposeAsync();
        _settings.Dispose();
        _stop.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task GreyThenAmberWithTheSimThenGreenWithADevice()
    {
        int changes = 0;
        _health.Changed += () => Interlocked.Increment(ref changes);
        Assert.Equal(HealthLight.Grey, _health.Current.Light);

        await _sim.SubscribeAsync([], CancellationToken.None);
        Assert.Equal(HealthLight.Amber, _health.Current.Light);
        Assert.Equal(1, changes);

        _usb.Plug("COM7", A);
        StartServing();

        await Eventually(() => _health.Current.Light == HealthLight.Green, "green");
        DeviceHealth device = Assert.Single(_health.Current.Devices);
        Assert.Equal("COM7", device.Port);
        Assert.Equal("dccc6c", device.Name);
        Assert.False(device.Assigned);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AnUnassignedArrivalIsAnnouncedAndALaterSettingsEditAssignsIt()
    {
        DeviceHealth? announced = null;
        int changes = 0;
        _health.UnassignedDeviceArrived += d => announced = d;
        _usb.Plug("COM7", A);
        StartServing();

        await Eventually(() => announced is not null, "the unassigned announcement");
        Assert.Equal(A, announced!.Identity.HardwareId);

        _health.Changed += () => Interlocked.Increment(ref changes);
        File.WriteAllText(_settings.FilePath, """{ "devices": { "dccc6c": { "name": "Radios", "panels": [ "com", "nope" ] } } }""");

        await Eventually(() => Volatile.Read(ref changes) > 0, "a change for the settings edit");
        DeviceHealth device = Assert.Single(_health.Current.Devices);
        Assert.Equal("Radios", device.Name);
        Assert.Equal(["com"], device.Panels); // the unknown panel shows nothing, so it is not listed
    }

    [Fact]
    public async Task AnAssignedArrivalIsNotAnnounced()
    {
        File.WriteAllText(_settings.FilePath, """{ "devices": { "dccc6c": { "panels": [ "nav" ] } } }""");
        await Eventually(() => _settings.Current.Devices.Count == 1, "the settings");
        bool announced = false;
        _health.UnassignedDeviceArrived += _ => announced = true;
        _usb.Plug("COM7", A);
        StartServing();

        await Eventually(() => _health.Current.Devices.Count == 1, "the device");
        Assert.False(announced);
    }

    [Fact]
    public async Task APortAnotherProgramHoldsShowsUpAndClearsWhenItIsLetGo()
    {
        int changes = 0;
        _health.Changed += () => Interlocked.Increment(ref changes);
        _usb.PlugBusy("COM5");
        StartServing();

        await Eventually(() => _health.Current.PortLines.SequenceEqual(["COM5: in use by another program"]), "COM5 listed as in use");
        Assert.Equal(1, Volatile.Read(ref changes));

        _usb.Release("COM5", A);

        await Eventually(() => _health.Current.Devices.Count == 1 && _health.Current.Ports.Count == 0, "the board on COM5");
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotStopTheOthers()
    {
        bool reached = false;
        _health.Changed += () => throw new InvalidOperationException("boom");
        _health.Changed += () => reached = true;

        await _sim.SubscribeAsync([], CancellationToken.None);

        Assert.True(reached);
        Assert.Equal(SimConnectionState.Connected, _health.Current.Sim);
    }
}
