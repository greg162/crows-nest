using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device.Protocol;
using Crowsnest.Device.Simulation;
using Crowsnest.Device.Transport;

namespace Crowsnest.Device.Tests;

public class DeviceConnectionTests
{
    private static readonly DeviceConnectionOptions FastHandshake =
        new() { HandshakeTimeout = TimeSpan.FromMilliseconds(500), PingInterval = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public async Task TheHandshakePopulatesCapabilitiesAndIdentity()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);

        Assert.Equal("crowpanel-2.1-rotary", device.Identity!.DeviceType);
        Assert.Equal("a4cb8fdccc6c", device.Identity.HardwareId);
        Assert.Equal(480, device.Capabilities!.Width);
        Assert.True(device.Capabilities.HasEncoder);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ASilentPortIsRejectedByTimeoutRatherThanHanging()
    {
        // This is how probing tells a device from a 3D printer on the next COM port.
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await deviceEnd.ConnectAsync(CancellationToken.None);

        await using var device = new DeviceConnection(
            hostEnd,
            new DeviceConnectionOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(100) });

        await Assert.ThrowsAsync<TimeoutException>(
            () => device.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARenderedFrameArrivesAtTheDeviceIntact()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);
        TaskCompletionSource<HostState> rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Rendered = state => rendered.TrySetResult(state);
        await device.RenderAsync(SelfTestFrames.HelloWorld(), CancellationToken.None);

        HostState state = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("HELLO WORLD", state.Fields[0].Text);
        Assert.Equal("single", state.Page.Layout);
        Assert.Equal(1, state.Revision);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ADeviceThatRestartsIsAckedAndReportedConnectedAgain()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);

        List<DeviceConnectionState> states = [];
        using IDisposable watch = device.ConnectionState.Subscribe(new Recorder(states));

        // The device crashed and came back: the port stayed open, and all the host sees is a hello.
        await fake.SendHelloAsync();

        await Eventually(() => fake.HelloAcks == 2 && states.Count == 3);
        Assert.Equal([DeviceConnectionState.Connected, DeviceConnectionState.Handshaking, DeviceConnectionState.Connected], states);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task EachDeviceGetsItsOwnBrightnessAndCanBeSentItAgain()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        int brightness = 35;
        await using var device = new DeviceConnection(hostEnd, FastHandshake with
        {
            BrightnessFor = identity => identity.HardwareId == "a4cb8fdccc6c" ? Volatile.Read(ref brightness) : null,
        });
        await device.ConnectAsync(CancellationToken.None);
        await Eventually(() => fake.HelloAcks == 1);
        Assert.Equal(35, fake.Brightness);

        Volatile.Write(ref brightness, 90);
        await device.RefreshConfigAsync(CancellationToken.None);

        await Eventually(() => fake.HelloAcks == 2);
        Assert.Equal(90, fake.Brightness);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ADeviceWithNoBrightnessOfItsOwnGetsTheDefault()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake with { Brightness = 70, BrightnessFor = _ => null });
        await device.ConnectAsync(CancellationToken.None);

        await Eventually(() => fake.HelloAcks == 1);
        Assert.Equal(70, fake.Brightness);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task AHelloFromADifferentDeviceFaultsTheLinkInsteadOfRejoining()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);

        await fake.SendHelloAsync("a4cb8fdc1234");

        await Eventually(() => device.Fault is not null);
        Assert.IsType<InvalidDataException>(device.Fault);
        Assert.Equal("a4cb8fdccc6c", device.Identity!.HardwareId);
        Assert.Equal(1, fake.HelloAcks);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ALinkTheDeviceClosesFaultsAtOnceWithTheReason()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        using var stopFake = new CancellationTokenSource();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(stopFake.Token);

        // A slow heartbeat, so only the end of the stream can explain a quick Faulted.
        await using var device = new DeviceConnection(hostEnd, FastHandshake with { PingInterval = TimeSpan.FromMinutes(1) });
        await device.ConnectAsync(CancellationToken.None);

        await stopFake.CancelAsync();
        await AwaitQuietly(fakeLoop);
        await deviceEnd.DisposeAsync(); // completes the device's side of the pipe: end of stream

        await Eventually(() => device.Fault is not null);
        Assert.IsType<EndOfStreamException>(device.Fault);
    }

    [Fact]
    public async Task DeviceInputReachesTheHostAsACoreEvent()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);
        await fake.TurnEncoderAsync(-3);

        DeviceInputEvent first = await FirstInputAsync(device);

        var turned = Assert.IsType<DeviceInputEvent.EncoderTurned>(first);
        Assert.Equal(-3, turned.Detents);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task GarbageOnTheLinkDoesNotBreakTheFramesEitherSideOfIt()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);

        await fake.SendRawAsync("this is not a frame at all\n");
        await fake.SendRawAsync("{\"v\":2,\"t\":\"input\",\"seq\":\n");
        await fake.TurnEncoderAsync(7);

        DeviceInputEvent first = await FirstInputAsync(device);

        Assert.Equal(7, Assert.IsType<DeviceInputEvent.EncoderTurned>(first).Detents);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ARoundTripIsMeasuredAgainstADeviceThatEchoesTheTimestampFaithfully()
    {
        // MeasureRoundTripAsync keys on Stopwatch.GetTimestamp(), which is well past
        // int.MaxValue on any machine that has been up a few minutes. The honest path
        // must survive that, which is the whole point of `ts` being 64-bit on both ends.
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd);
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(hostEnd, FastHandshake);
        await device.ConnectAsync(CancellationToken.None);

        TimeSpan elapsed = await device.MeasureRoundTripAsync(CancellationToken.None);

        Assert.True(elapsed >= TimeSpan.Zero);

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    [Fact]
    public async Task ADeviceThatEchoesATruncatedTimestampTimesOutRatherThanHanging()
    {
        // The original defect: the device narrowed `ts` to 32 bits, so no pong ever matched
        // the key its ping was stored under and the await never completed. The link stayed
        // up and the tool printed nothing, which is the worst possible way to fail.
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        await using var fake = new SimulatedDevice(deviceEnd) { SaturatePongTimestampToInt32 = true };
        Task fakeLoop = fake.RunAsync(CancellationToken.None);

        await using var device = new DeviceConnection(
            hostEnd,
            FastHandshake with { PingTimeout = TimeSpan.FromMilliseconds(200) });
        await device.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(
            () => device.MeasureRoundTripAsync(CancellationToken.None));

        await device.DisposeAsync();
        await AwaitQuietly(fakeLoop);
    }

    private static async Task<DeviceInputEvent> FirstInputAsync(DeviceConnection device)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (DeviceInputEvent input in device.Inputs.WithCancellation(timeout.Token))
        {
            return input;
        }

        throw new InvalidOperationException("The input stream completed without producing an event.");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Records every state the connection reports, from whichever thread reports it.</summary>
    private sealed class Recorder(List<DeviceConnectionState> states) : IObserver<DeviceConnectionState>
    {
        public void OnNext(DeviceConnectionState value)
        {
            lock (states)
            {
                states.Add(value);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // The loopback was torn down underneath the fake device's loop.
        }
    }
}
