using System.Text;
using Crowsnest.Device.Protocol;

namespace Crowsnest.Device.Tests.Protocol;

/// <summary>
/// Exercises the corpus at tests/protocol-corpus/malformed.ndjson, which the firmware's
/// Unity host test for crowsnest_link reads as well (spec §11.1). A frame that only one
/// end rejects is precisely the bug this arrangement catches, so the two suites must stay
/// pointed at the same file.
/// </summary>
public class MalformedFrameCorpusTests
{
    public static TheoryData<string> Corpus()
    {
        TheoryData<string> data = [];
        foreach (string line in ProtocolCorpus.Lines())
        {
            data.Add(line);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void NoCorpusLineThrows(string line)
    {
        // The contract is not "rejected" — some of these are legal frames carrying members
        // this version has never heard of. The contract is that decoding never throws, so
        // one bad frame costs one frame and not the link.
        byte[] utf8 = Encoding.UTF8.GetBytes(line);

        bool decodedDevice = ProtocolCodec.TryDecodeDevice(utf8, out DeviceMessage? device);
        bool decodedHost = ProtocolCodec.TryDecodeHost(utf8, out HostMessage? host);

        // The out parameter and the return value must agree, whichever way they land.
        Assert.Equal(decodedDevice, device is not null);
        Assert.Equal(decodedHost, host is not null);
    }

    [Fact]
    public void TheCorpusIsNotEmpty()
    {
        // A path typo would otherwise turn every test above into a silent pass.
        Assert.True(ProtocolCorpus.Lines().Count > 20, "Corpus file missing or not copied to the output directory.");
    }

    [Theory]
    [InlineData("""{"v":2,"t":"telemetry","cpu":42}""")]
    [InlineData("""{"v":2,"t":"ota_status","state":"writing","pct":42}""")]
    public void AnUnknownMessageTypeIsIgnoredRatherThanFatal(string frame)
    {
        Assert.False(ProtocolCodec.TryDecodeDevice(Encoding.UTF8.GetBytes(frame), out DeviceMessage? message));
        Assert.Null(message);
    }

    [Fact]
    public void UnknownMembersOnAKnownTypeAreSkipped()
    {
        const string Frame = """{"v":2,"t":"pong","ts":918273,"jitter":4,"nested":{"a":[1,2,3]}}""";

        Assert.True(ProtocolCodec.TryDecodeDevice(Encoding.UTF8.GetBytes(Frame), out DeviceMessage? message));
        Assert.Equal(918273, Assert.IsType<DevicePong>(message).Timestamp);
    }

    [Fact]
    public void AFrameFromAFutureProtocolVersionStillDecodes()
    {
        // Version is negotiated in the handshake, not enforced per frame — the two sides
        // version independently (spec §6.1).
        Assert.True(ProtocolCodec.TryDecodeDevice("""{"v":9,"t":"pong","ts":1}"""u8, out DeviceMessage? message));
        Assert.Equal(9, message!.Version);
    }

    [Fact]
    public void AKnownTypeMissingRequiredMembersIsRejected()
    {
        Assert.False(ProtocolCodec.TryDecodeDevice(
            """{"v":2,"t":"hello","dev":"crowpanel-2.1-rotary"}"""u8,
            out DeviceMessage? message));
        Assert.Null(message);
    }
}

internal static class ProtocolCorpus
{
    public static IReadOnlyList<string> Lines()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "protocol-corpus", "malformed.ndjson");
        if (!File.Exists(path))
        {
            return [];
        }

        return
        [
            .. File.ReadAllLines(path)
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
        ];
    }
}
