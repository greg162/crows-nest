using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;
using Crowsnest.Device.Transport;

namespace Crowsnest.DeviceSimulator;

/// <summary>
/// The whole PC side of the vertical slice, with no hardware: Core builds the frames,
/// Crowsnest.Device encodes and writes them, and SimulatedDevice decodes and "renders" them.
/// Swap LoopbackTransport for SerialPortTransport and the same code drives a real board.
/// Run with <c>--selftest</c>: renders the self-test frames and sends a few inputs back.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();

        await using var simulated = new SimulatedDevice(deviceEnd);
        simulated.Rendered = state => Console.WriteLine(ConsoleDeviceRenderer.Render(state));
        simulated.NoticeShown = notice => Console.WriteLine($"    notice: {notice.Kind}");

        Task simulatedLoop = simulated.RunAsync(cts.Token);

        await using var device = new DeviceConnection(hostEnd);
        device.LogReceived = log => Console.WriteLine($"    [{log.Level}] {log.Message}");

        await device.ConnectAsync(cts.Token);

        DeviceCapabilities caps = device.Capabilities!;
        Console.WriteLine($"connected to {device.Identity!.DeviceType} {device.Identity.FirmwareVersion}");
        Console.WriteLine($"  id {device.Identity.HardwareId}  {caps.Width}x{caps.Height} {caps.Shape}");
        Console.WriteLine($"  encoder {caps.DetentsPerClick}/click, touch {caps.HasTouch}, {caps.MaxFields} fields");
        Console.WriteLine($"  layouts {string.Join(", ", caps.Layouts)}");
        Console.WriteLine();

        // Report inputs as they arrive, the way PanelCoordinator will in phase 1.
        Task inputs = Task.Run(
            async () =>
            {
                await foreach (DeviceInputEvent input in device.Inputs)
                {
                    Console.WriteLine($"    input #{input.Sequence}: {input}");
                }
            },
            CancellationToken.None);

        foreach (DisplayFrame frame in SelfTestFrames.All())
        {
            await device.RenderAsync(frame, cts.Token);
            await Task.Delay(150, cts.Token);
        }

        // And back the other way: the encoder, the knob and the screen.
        await simulated.TurnEncoderAsync(-3, cts.Token);
        await simulated.PressKnobAsync(held: false, ct: cts.Token);
        await simulated.TapAsync(240, 180, cts.Token);
        await Task.Delay(150, cts.Token);

        // A stale frame must be dropped by the device, not rendered (spec §6.1).
        await device.RenderAsync(SelfTestFrames.HelloWorld(revision: 1), cts.Token);
        await Task.Delay(150, cts.Token);
        Console.WriteLine($"stale frames dropped by the device: {simulated.StaleFramesDropped}");

        await cts.CancelAsync();
        await Task.WhenAny(simulatedLoop, inputs, Task.Delay(500, CancellationToken.None));
    }
}
