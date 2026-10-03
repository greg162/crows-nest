using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Crowsnest.Device.Protocol;

namespace Crowsnest.Device.Tests.Protocol;

public class NdjsonFramingTests
{
    [Fact]
    public async Task FramesSplitAcrossReadsAreReassembled()
    {
        // A USB CDC read can land anywhere, including mid-token. This is the failure the
        // pipeline exists to prevent.
        string[] chunks = ["""{"v":2,"t":"po""", """ng","ts":1}""", "\n", """{"v":2,"t":"pong","ts":2}""", "\n"];

        IReadOnlyList<string> frames = await ReadFramesAsync(chunks);

        Assert.Equal(2, frames.Count);
        Assert.Equal("""{"v":2,"t":"pong","ts":1}""", frames[0]);
    }

    [Fact]
    public async Task SeveralFramesInOneReadAreAllDelivered()
    {
        IReadOnlyList<string> frames = await ReadFramesAsync(["{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n"]);

        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task CarriageReturnsAreStripped()
    {
        // Anything speaking to the link through a terminal will send CRLF.
        IReadOnlyList<string> frames = await ReadFramesAsync(["{\"a\":1}\r\n"]);

        Assert.Equal("""{"a":1}""", frames[0]);
    }

    [Fact]
    public async Task BlankLinesAreSkipped()
    {
        IReadOnlyList<string> frames = await ReadFramesAsync(["\n\n{\"a\":1}\n\n"]);

        Assert.Single(frames);
    }

    [Fact]
    public async Task APartialFrameAtEndOfStreamIsDropped()
    {
        // The cable was pulled mid-frame. Half a frame is not a frame.
        IReadOnlyList<string> frames = await ReadFramesAsync(["{\"a\":1}\n{\"a\":2"]);

        Assert.Single(frames);
    }

    [Fact]
    public async Task AnOversizedFrameIsDiscardedAndTheLinkResynchronises()
    {
        // A firmware stuck mid-frame, or noise on the line, must not grow the buffer
        // without bound or take the following good frames down with it.
        var pipe = new Pipe();
        var reader = new NdjsonFrameReader(pipe.Reader, maxFrameBytes: 64);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 4096)));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("still-garbage\n{\"a\":1}\n"));
        await pipe.Writer.CompleteAsync();

        List<string> frames = [];
        await foreach (byte[] frame in reader.ReadAllAsync())
        {
            frames.Add(Encoding.UTF8.GetString(frame));
        }

        Assert.Equal(1, reader.OversizedFrames);
        Assert.Equal(["""{"a":1}"""], frames);
    }

    [Fact]
    public async Task ArbitraryBinaryGarbageProducesFramesRatherThanAnException()
    {
        // ESP32 bootloader chatter at the wrong baud rate looks exactly like this.
        byte[] noise = new byte[512];
        new Random(1234).NextBytes(noise);

        var pipe = new Pipe();
        var reader = new NdjsonFrameReader(pipe.Reader);
        await pipe.Writer.WriteAsync(noise);
        await pipe.Writer.WriteAsync("\n{\"v\":2,\"t\":\"pong\",\"ts\":1}\n"u8.ToArray());
        await pipe.Writer.CompleteAsync();

        List<byte[]> frames = [];
        await foreach (byte[] frame in reader.ReadAllAsync())
        {
            frames.Add(frame);
        }

        // The noise decodes to nothing; the good frame that follows still arrives.
        Assert.Contains(frames, f => ProtocolCodec.TryDecodeDevice(f, out DeviceMessage? m) && m is DevicePong);
    }

    [Fact]
    public async Task WrittenFramesAreOneLineEachAndNewlineTerminated()
    {
        var pipe = new Pipe();
        var writer = new NdjsonFrameWriter(pipe.Writer);

        await writer.WriteAsync(new HostPing { Timestamp = 918273 });
        await writer.WriteAsync(new HostNotice { Kind = "aircraft_rejected" });
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAtLeastAsync(1);
        string text = Encoding.UTF8.GetString(result.Buffer.ToArray());

        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> ReadFramesAsync(IEnumerable<string> chunks)
    {
        var pipe = new Pipe();
        var reader = new NdjsonFrameReader(pipe.Reader);

        foreach (string chunk in chunks)
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(chunk));
        }

        await pipe.Writer.CompleteAsync();

        List<string> frames = [];
        await foreach (byte[] frame in reader.ReadAllAsync())
        {
            frames.Add(Encoding.UTF8.GetString(frame));
        }

        return frames;
    }
}
