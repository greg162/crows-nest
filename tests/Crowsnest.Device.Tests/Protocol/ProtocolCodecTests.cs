using System.Text;
using System.Text.Json;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device.Protocol;

namespace Crowsnest.Device.Tests.Protocol;

public class ProtocolCodecTests
{
    [Fact]
    public void ComPageSerialisesToTheFrameTheSpecDocuments()
    {
        // Spec §6.1's worked example, near enough to compare field by field. If this
        // changes, the firmware's parser changes with it.
        HostState state = ProtocolCodec.ToWire(
            new DisplayFrame(
                Revision: 1057,
                Sim: SimConnectionState.Connected,
                Page: new PageDescriptor("com1", "COM 1", PageLayout.ActiveStandbyPair, Index: 0, Count: 6),
                Fields:
                [
                    new FieldDescriptor(FieldRole.Primary, "STBY", "121.500", CursorSpan: 4..7, Pending: true),
                    new FieldDescriptor(FieldRole.Secondary, "ACTIVE", "122.800"),
                ],
                AckSequence: 44));

        using JsonDocument json = JsonDocument.Parse(Serialise(state));
        JsonElement root = json.RootElement;

        Assert.Equal(2, root.GetProperty("v").GetInt32());
        Assert.Equal("state", root.GetProperty("t").GetString());
        Assert.Equal(1057, root.GetProperty("rev").GetInt64());
        Assert.Equal(44, root.GetProperty("ack").GetInt64());
        Assert.Equal("connected", root.GetProperty("sim").GetString());
        Assert.Equal("pair", root.GetProperty("page").GetProperty("layout").GetString());

        JsonElement primary = root.GetProperty("fields")[0];
        Assert.Equal("primary", primary.GetProperty("role").GetString());
        Assert.Equal("121.500", primary.GetProperty("text").GetString());
        Assert.Equal([4, 7], primary.GetProperty("cursor").EnumerateArray().Select(e => e.GetInt32()));
        Assert.True(primary.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void AFieldWithNoCursorOmitsTheCursorMember()
    {
        HostState state = ProtocolCodec.ToWire(SelfTestFrames.HelloWorld());

        using JsonDocument json = JsonDocument.Parse(Serialise(state));

        Assert.False(json.RootElement.GetProperty("fields")[0].TryGetProperty("cursor", out _));
    }

    [Theory]
    [InlineData(PageLayout.ActiveStandbyPair, "pair")]
    [InlineData(PageLayout.SingleValue, "single")]
    [InlineData(PageLayout.DualValue, "dual")]
    public void LayoutNamesRoundTrip(PageLayout layout, string wire)
    {
        Assert.Equal(wire, ProtocolCodec.ToWire(layout));
        Assert.Equal(layout, ProtocolCodec.ParseLayout(wire));
    }

    [Fact]
    public void AnUnknownLayoutNameIsDroppedRatherThanRejected()
    {
        // A future firmware may advertise a layout this host has never heard of.
        Assert.Null(ProtocolCodec.ParseLayout("hexagonal"));
    }

    [Fact]
    public void DeviceHelloBecomesCapabilitiesAndIdentity()
    {
        const string Frame = """
            {"v":2,"t":"hello","dev":"crowpanel-2.1-rotary","fw":"1.0.0","id":"a4cf12de9010",
             "caps":{"shape":"round","w":480,"h":480,"encoder":true,"detentsPerClick":4,
                     "touch":true,"buttons":1,"maxFields":3,"layouts":["pair","single","dual","hexagonal"]}}
            """;

        Assert.True(ProtocolCodec.TryDecodeDevice(Encoding.UTF8.GetBytes(Frame), out DeviceMessage? message));
        DeviceHello hello = Assert.IsType<DeviceHello>(message);

        DeviceCapabilities caps = ProtocolCodec.ToCapabilities(hello);
        Assert.Equal(ScreenShape.Round, caps.Shape);
        Assert.Equal(480, caps.Width);
        Assert.Equal(4, caps.DetentsPerClick);
        Assert.Equal(3, caps.MaxFields);
        Assert.Equal(
            [PageLayout.ActiveStandbyPair, PageLayout.SingleValue, PageLayout.DualValue],
            caps.Layouts);

        DeviceIdentity identity = ProtocolCodec.ToIdentity(hello);
        Assert.Equal("a4cf12de9010", identity.HardwareId);
        Assert.Equal("crowpanel-2.1-rotary", identity.DeviceType);
    }

    [Theory]
    [InlineData("""{"v":2,"t":"input","seq":42,"ev":"encoder","d":-3}""", typeof(DeviceInputEvent.EncoderTurned))]
    [InlineData("""{"v":2,"t":"input","seq":43,"ev":"press","kind":"short"}""", typeof(DeviceInputEvent.KnobPressed))]
    [InlineData("""{"v":2,"t":"input","seq":44,"ev":"tap","x":240,"y":180}""", typeof(DeviceInputEvent.ScreenTapped))]
    [InlineData("""{"v":2,"t":"input","seq":45,"ev":"swipe","dir":"left"}""", typeof(DeviceInputEvent.SwipeDetected))]
    public void EveryInputFrameInTheSpecDecodes(string frame, Type expected)
    {
        Assert.True(ProtocolCodec.TryDecodeDevice(Encoding.UTF8.GetBytes(frame), out DeviceMessage? message));
        DeviceInputFrame input = Assert.IsType<DeviceInputFrame>(message);

        DeviceInputEvent? evt = ProtocolCodec.ToInputEvent(input, DateTimeOffset.UnixEpoch);

        Assert.IsType(expected, evt);
    }

    [Fact]
    public void EncoderDetentsKeepTheirSign()
    {
        ProtocolCodec.TryDecodeDevice(
            """{"v":2,"t":"input","seq":42,"ev":"encoder","d":-3}"""u8,
            out DeviceMessage? message);

        var turned = (DeviceInputEvent.EncoderTurned)
            ProtocolCodec.ToInputEvent((DeviceInputFrame)message!, DateTimeOffset.UnixEpoch)!;

        Assert.Equal(-3, turned.Detents);
    }

    private static string Serialise(HostMessage message) =>
        JsonSerializer.Serialize(message, ProtocolJsonContext.Default.HostMessage);
}
