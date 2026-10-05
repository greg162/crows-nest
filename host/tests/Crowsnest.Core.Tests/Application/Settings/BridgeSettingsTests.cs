using System.Text;
using Crowsnest.Core.Application.Settings;

namespace Crowsnest.Core.Tests.Application.Settings;

/// <summary>The settings file (spec §6.2, §8): devices by hardware id, their panels and brightness.</summary>
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
                "ccc6c1": { "name": "Radios", "panels": [ "com", "nav" ], "brightness": 60, },
                "a4cb8fdc1234": { "panels": [ "nav" ] },
            } }
            """);

        Assert.Equal(2, settings.Devices.Count);
        DeviceSettings radios = settings.Devices["ccc6c1"];
        Assert.Equal("Radios", radios.Name);
        Assert.Equal(["com", "nav"], radios.Panels);
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
        Assert.Empty(Read("""{ "devices": { "ccc6c1": { "name": "Spare" } } }""").Devices["ccc6c1"].Panels);

    [Fact]
    public void ADeviceIsFoundByItsFullId() =>
        Assert.Equal(["nav"], Read($$"""{ "devices": { "{{FullId}}": { "panels": [ "nav" ] } } }""").Find(FullId)!.Panels);

    /// <summary>The six its screen shows: the waiting screen's "PANEL dccc6c", the unassigned screen's value.</summary>
    [Fact]
    public void ADeviceIsFoundByTheLastSixCharacters() =>
        Assert.Equal(["com"], Read("""{ "devices": { "dccc6c": { "panels": [ "com" ] } } }""").Find(FullId)!.Panels);

    [Fact]
    public void CaseDoesNotMatter() =>
        Assert.NotNull(Read("""{ "devices": { "DCCC6C": { "panels": [ "com" ] } } }""").Find(FullId.ToUpperInvariant()));

    [Fact]
    public void SixCharactersFromElsewhereInTheIdDoNotMatch() =>
        Assert.Null(Read("""{ "devices": { "a4cb8f": { "panels": [ "com" ] } } }""").Find(FullId));

    [Fact]
    public void TheFullIdWinsOverTheLastSix()
    {
        BridgeSettings settings = Read($$"""{ "devices": { "dccc6c": { "panels": [ "com" ] }, "{{FullId}}": { "panels": [ "nav" ] } } }""");

        Assert.Equal(["nav"], settings.Find(FullId)!.Panels);
    }

    [Fact]
    public void AnUnlistedDeviceIsNotFound() =>
        Assert.Null(Read("""{ "devices": { "123456": { "panels": [ "com" ] } } }""").Find(FullId));

    [Theory]
    [InlineData("ccc6c")]
    [InlineData("ccc6c1x")]
    [InlineData("zzzzzz")]
    [InlineData("com4")]
    public void ADeviceKeyMustBeAHardwareIdOrItsLastSix(string key) =>
        AssertRejected($$"""{ "devices": { "{{key}}": { "panels": [ "com" ] } } }""", $"device '{key}'");

    [Fact]
    public void AnUnknownKeyIsReportedNotIgnored() =>
        AssertRejected("""{ "devices": { "ccc6c1": { "panel": [ "com" ] } } }""", "$.devices.ccc6c1.panel: that is not a setting");

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
        AssertRejected("""{ "devices": { "ccc6c1": { "panels": [ "com", " " ] } } }""", "empty entry");

    [Theory]
    [InlineData("")]
    [InlineData("{ \"devices\": ")]
    [InlineData("[ ]")]
    public void MalformedJsonIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Read(json));

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        BridgeSettings settings = Read("""{ "devices": { "ccc6c1": { "name": "Radios", "panels": [ "com" ], "brightness": 40 }, "123456": { } } }""");

        using MemoryStream json = new();
        settings.Write(json);
        json.Position = 0;
        BridgeSettings again = BridgeSettings.Read(json);

        Assert.Equal(["com"], again.Devices["ccc6c1"].Panels);
        Assert.Equal(40, again.Devices["ccc6c1"].Brightness);
        Assert.Equal("Radios", again.Devices["ccc6c1"].Name);
        Assert.Empty(again.Devices["123456"].Panels);
    }
}
