using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;
using Crowsnest.Device.Transport;
using Crowsnest.DeviceSimulator;

// The whole PC side of the vertical slice, with no hardware: Core builds the frames,
// Crowsnest.Device encodes and writes them, and SimulatedPanel decodes and "renders" them.
// Swap LoopbackTransport for SerialPortTransport and the same code drives a real board.

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

(LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();

await using var panel = new SimulatedPanel(deviceEnd);
panel.Rendered = state => Console.WriteLine(ConsolePanelRenderer.Render(state));
panel.NoticeShown = notice => Console.WriteLine($"    notice: {notice.Kind}");

Task panelLoop = panel.RunAsync(cts.Token);

await using var device = new PanelDeviceConnection(hostEnd);
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
await panel.TurnEncoderAsync(-3, cts.Token);
await panel.PressKnobAsync(held: false, ct: cts.Token);
await panel.TapAsync(240, 180, cts.Token);
await Task.Delay(150, cts.Token);

// A stale frame must be dropped by the device, not rendered (spec §6.1).
await device.RenderAsync(SelfTestFrames.HelloWorld(revision: 1), cts.Token);
await Task.Delay(150, cts.Token);
Console.WriteLine($"stale frames dropped by the panel: {panel.StaleFramesDropped}");

await cts.CancelAsync();
await Task.WhenAny(panelLoop, inputs, Task.Delay(500, CancellationToken.None));
