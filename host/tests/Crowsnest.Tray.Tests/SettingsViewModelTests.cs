using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Application.Settings;
using Crowsnest.Host;
using Crowsnest.Tray.Settings;

namespace Crowsnest.Tray.Tests;

/// <summary>The settings window's model (spec §8): what it lists, and the file it saves.</summary>
public class SettingsViewModelTests
{
    private const string Radios = "a4cb8fdccc6c";
    private const string Spare = "a4cb8f9f44a1";
    private const int DefaultBrightness = 80;

    private static readonly IReadOnlyList<PanelPages> Available =
    [
        new("com", [Page("com1", "COM1"), Page("com2", "COM2")]),
        new("nav", [Page("nav1", "NAV1"), Page("nav2", "NAV2")]),
        new("xpdr", []),
    ];

    private static PanelPage Page(string id, string title) => new(id, title, PageLayout.ActiveStandbyPair, []);

    private static DeviceHealth Plugged(string hardwareId, string port) =>
        new(new DeviceIdentity(hardwareId, "crowpanel-2.1-rotary", "0.3.4"), port, hardwareId[^6..], null);

    private static BridgeSettings Saved(params (string Key, DeviceSettings Device)[] devices) =>
        new(devices.ToDictionary(d => d.Key, d => d.Device));

    private static SettingsViewModel Model(BridgeSettings saved, params DeviceHealth[] connected) =>
        new(saved, Available, connected, DefaultBrightness);

    [Fact]
    public void PluggedInDevicesComeFirstThenTheRestInFileOrder()
    {
        SettingsViewModel model = Model(
            Saved(("111111", new("Old", "com", null)), ("dccc6c", new("Radios", "com", null))),
            Plugged(Radios, "COM4"),
            Plugged(Spare, "COM5"));

        Assert.Equal(["dccc6c", "9f44a1", "111111"], model.Devices.Select(d => d.Key));
        Assert.Equal(["COM4", "COM5", null], model.Devices.Select(d => d.Port));
        Assert.Same(model.Devices[0], model.Selected);
    }

    [Fact]
    public void APluggedInDeviceUsesTheEntryTheBridgeUses()
    {
        SettingsViewModel model = Model(Saved((Radios, new("Radios", "nav", null))), Plugged(Radios, "COM4"));

        DeviceSettingsViewModel device = Assert.Single(model.Devices);
        Assert.Equal(Radios, device.Key);
        Assert.Equal("dccc6c", device.ShortId);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void TheChoicesAreNoneThenEachPanel()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new(null, "nav", null))));

        DeviceSettingsViewModel device = model.Devices[0];
        Assert.Equal([null, "com", "nav", "xpdr"], device.Choices.Select(c => c.Id));
        Assert.Equal("nav", device.Panel.Id);
        Assert.Equal("NAV", device.Summary);
        Assert.Equal("NAV1, NAV2", device.Choices[2].Pages);
        Assert.Equal("no pages yet", device.Choices[3].Pages);
    }

    [Fact]
    public void ChoosingAnotherPanelReplacesTheOne()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new(null, "com", null))));
        DeviceSettingsViewModel device = model.Devices[0];

        device.Panel = device.Choices.Single(c => c.Id == "nav");

        Assert.Equal("nav", model.ToSettings().Devices["dccc6c"].Panel);
    }

    [Fact]
    public void ChoosingNoneUnassignsTheDevice()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new("Radios", "com", null))));
        DeviceSettingsViewModel device = model.Devices[0];

        device.Panel = PanelChoiceViewModel.None;

        Assert.Equal("not assigned", device.Summary);
        Assert.Null(model.ToSettings().Devices["dccc6c"].Panel);
    }

    [Fact]
    public void APanelThisVersionDoesNotHaveIsKeptRatherThanDropped()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new(null, "ap", null))));

        PanelChoiceViewModel ap = model.Devices[0].Panel;
        Assert.Equal("ap", ap.Id);
        Assert.Equal("not part of this version of Crowsnest", ap.Pages);
        Assert.Equal("ap", model.ToSettings().Devices["dccc6c"].Panel);
    }

    [Fact]
    public void SavingUnchangedSettingsWritesWhatWasRead()
    {
        BridgeSettings saved = Saved(
            ("dccc6c", new("Radios", "com", 60)),
            (Spare, new(null, "nav", null)));

        BridgeSettings written = Model(saved, Plugged(Radios, "COM4")).ToSettings();

        Assert.Equal(saved.Devices.Keys.Order(), written.Devices.Keys.Order());
        foreach ((string key, DeviceSettings device) in saved.Devices)
        {
            Assert.Equal(device.Name, written.Devices[key].Name);
            Assert.Equal(device.Panel, written.Devices[key].Panel);
            Assert.Equal(device.Brightness, written.Devices[key].Brightness);
        }
    }

    [Fact]
    public void ANewDeviceIsKeyedByWhatItsScreenShowsAndLeftOutUntilSomethingIsSet()
    {
        SettingsViewModel model = Model(BridgeSettings.Empty, Plugged(Radios, "COM4"));
        DeviceSettingsViewModel device = Assert.Single(model.Devices);
        Assert.Equal("dccc6c", device.Key);
        Assert.Equal("not assigned", device.Summary);
        Assert.Empty(model.ToSettings().Devices);

        device.Panel = device.Choices.Single(c => c.Id == "com");

        Assert.Equal("com", model.ToSettings().Devices["dccc6c"].Panel);
    }

    [Fact]
    public void BrightnessShowsTheDefaultButIsOnlyWrittenOnceSet()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new(null, "com", null))));
        DeviceSettingsViewModel device = model.Devices[0];
        Assert.Equal(DefaultBrightness, device.Brightness);
        Assert.Null(model.ToSettings().Devices["dccc6c"].Brightness);

        device.Brightness = 140;

        Assert.Equal(100, model.ToSettings().Devices["dccc6c"].Brightness);
    }

    [Fact]
    public void ANameIsTrimmedAndABlankOneIsLeftOut()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new("Radios", "com", null))));
        DeviceSettingsViewModel device = model.Devices[0];

        device.Name = "  Left radios ";
        Assert.Equal("Left radios", model.ToSettings().Devices["dccc6c"].Name);
        Assert.Equal("Left radios", device.Title);

        device.Name = "   ";
        Assert.Null(model.ToSettings().Devices["dccc6c"].Name);
        Assert.Equal("dccc6c", device.Title);
    }

    [Fact]
    public void OnlyADeviceThatIsNotPluggedInCanBeForgotten()
    {
        SettingsViewModel model = Model(
            Saved(("dccc6c", new(null, "com", null)), ("111111", new(null, "nav", null))),
            Plugged(Radios, "COM4"));

        Assert.False(model.CanForget);
        model.ForgetSelected();
        Assert.Equal(2, model.Devices.Count);

        model.Selected = model.Devices[1];
        Assert.True(model.CanForget);
        model.ForgetSelected();

        Assert.Equal(["dccc6c"], model.ToSettings().Devices.Keys);
        Assert.Same(model.Devices[0], model.Selected);
    }

    [Fact]
    public void DevicesPluggedInAndPulledOutWhileOpenAreFollowed()
    {
        SettingsViewModel model = Model(Saved(("dccc6c", new(null, "com", null))), Plugged(Radios, "COM4"));

        model.ShowConnected([Plugged(Spare, "COM5")]);

        Assert.Equal(["dccc6c", "9f44a1"], model.Devices.Select(d => d.Key));
        Assert.False(model.Devices[0].IsConnected);
        Assert.Equal("Not connected", model.Devices[0].Status);
        Assert.Equal("Connected on COM5", model.Devices[1].Status);
    }

    [Fact]
    public void ADeviceIsSelectedByItsHardwareId()
    {
        SettingsViewModel model = Model(BridgeSettings.Empty, Plugged(Radios, "COM4"), Plugged(Spare, "COM5"));

        model.Select(Spare.ToUpperInvariant());

        Assert.Equal("9f44a1", model.Selected!.Key);
    }

    [Fact]
    public void WithNoDevicesNothingIsSelected()
    {
        SettingsViewModel model = Model(BridgeSettings.Empty);

        Assert.True(model.HasNoDevices);
        Assert.Null(model.Selected);
        Assert.False(model.CanForget);
    }
}
