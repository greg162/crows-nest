using System.Text;
using Crowsnest.Core.Application.Settings;

namespace Crowsnest.Core.Tests.Application.Settings;

/// <summary>The settings file (spec §6.2, §8): devices by hardware id, the panel each shows and its brightness.</summary>
public class BridgeSettingsTests
{
    private const string FullId = "a4cb8fdccc6c";

    private static BridgeSettings Read(string json) => BridgeSettings.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static void AssertRejected(string json, string expected)
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Read(json));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DevicesAreReadWithCommentsAndTrailingCommas()
    {
        BridgeSettings settings = Read("""
            // mine
            { "devices": {
                "ccc6c1": { "name": "Radios", "panel": "com", "brightness": 60, },
                "a4cb8fdc1234": { "panel": "nav" },
            } }
            """);

        Assert.Equal(2, settings.Devices.Count);
        DeviceSettings radios = settings.Devices["ccc6c1"];
        Assert.Equal("Radios", radios.Name);
        Assert.Equal("com", radios.Panel);
        Assert.Equal(60, radios.Brightness);
        Assert.Null(settings.Devices["a4cb8fdc1234"].Brightness);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"devices\": { } }")]
    public void AFileWithoutDevicesHasNone(string json) =>
        Assert.Empty(Read(json).Devices);

    [Fact]
    public void ADeviceWithoutPanelsIsUnassigned() =>
        Assert.Null(Read("""{ "devices": { "ccc6c1": { "name": "Spare" } } }""").Devices["ccc6c1"].Panel);

    [Fact]
    public void ADeviceIsFoundByItsFullId() =>
        Assert.Equal("nav", Read($$"""{ "devices": { "{{FullId}}": { "panel": "nav" } } }""").Find(FullId)!.Panel);

    /// <summary>The six its screen shows: the waiting screen's "PANEL dccc6c", the unassigned screen's value.</summary>
    [Fact]
    public void ADeviceIsFoundByTheLastSixCharacters() =>
        Assert.Equal("com", Read("""{ "devices": { "dccc6c": { "panel": "com" } } }""").Find(FullId)!.Panel);

    [Fact]
    public void CaseDoesNotMatter() =>
        Assert.NotNull(Read("""{ "devices": { "DCCC6C": { "panel": "com" } } }""").Find(FullId.ToUpperInvariant()));

    [Fact]
    public void SixCharactersFromElsewhereInTheIdDoNotMatch() =>
        Assert.Null(Read("""{ "devices": { "a4cb8f": { "panel": "com" } } }""").Find(FullId));

    [Fact]
    public void TheFullIdWinsOverTheLastSix()
    {
        BridgeSettings settings = Read($$"""{ "devices": { "dccc6c": { "panel": "com" }, "{{FullId}}": { "panel": "nav" } } }""");

        Assert.Equal("nav", settings.Find(FullId)!.Panel);
    }

    [Theory]
    [InlineData("""{ "devices": { "dccc6c": { } } }""", "dccc6c")]
    [InlineData("""{ "devices": { "a4cb8fdccc6c": { } } }""", FullId)]
    [InlineData("""{ "devices": { "dccc6c": { }, "a4cb8fdccc6c": { } } }""", FullId)]
    [InlineData("""{ "devices": { "123456": { } } }""", null)]
    public void TheKeyIsTheEntryFindUses(string json, string? expected) =>
        Assert.Equal(expected, Read(json).KeyFor(FullId.ToUpperInvariant()));

    [Fact]
    public void AnUnlistedDeviceIsNotFound() =>
        Assert.Null(Read("""{ "devices": { "123456": { "panel": "com" } } }""").Find(FullId));

    [Theory]
    [InlineData("ccc6c")]
    [InlineData("ccc6c1x")]
    [InlineData("zzzzzz")]
    [InlineData("com4")]
    public void ADeviceKeyMustBeAHardwareIdOrItsLastSix(string key) =>
        AssertRejected($$"""{ "devices": { "{{key}}": { "panel": "com" } } }""", $"device '{key}'");

    [Fact]
    public void AnUnknownKeyIsReportedNotIgnored() =>
        AssertRejected("""{ "devices": { "ccc6c1": { "pannel": "com" } } }""", "$.devices.ccc6c1.pannel: that is not a setting");

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void BrightnessGoesFromZeroToAHundred(int brightness) =>
        AssertRejected($$"""{ "devices": { "ccc6c1": { "brightness": {{brightness}} } } }""", "brightness");

    [Fact]
    public void TheSameDeviceTwiceIsRejected() =>
        AssertRejected("""{ "devices": { "ccc6c1": { }, "CCC6C1": { } } }""", "listed twice");

    [Fact]
    public void AnEmptyPanelNameIsRejected() =>
        AssertRejected("""{ "devices": { "ccc6c1": { "panel": " " } } }""", "\"panel\" is empty");

    [Fact]
    public void TheOldListOfPanelsIsReportedNotIgnored() =>
        AssertRejected("""{ "devices": { "ccc6c1": { "panels": [ "com" ] } } }""", "$.devices.ccc6c1.panels: that is not a setting");

    [Theory]
    [InlineData("")]
    [InlineData("{ \"devices\": ")]
    [InlineData("[ ]")]
    public void MalformedJsonIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Read(json));

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        BridgeSettings settings = Read("""{ "devices": { "ccc6c1": { "name": "Radios", "panel": "com", "brightness": 40 }, "123456": { } } }""");

        using MemoryStream json = new();
        settings.Write(json);
        json.Position = 0;
        BridgeSettings again = BridgeSettings.Read(json);

        Assert.Equal("com", again.Devices["ccc6c1"].Panel);
        Assert.Equal(40, again.Devices["ccc6c1"].Brightness);
        Assert.Equal("Radios", again.Devices["ccc6c1"].Name);
        Assert.Null(again.Devices["123456"].Panel);
    }
}
