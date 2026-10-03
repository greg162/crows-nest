using System.Diagnostics;
using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;
using Crowsnest.Device.Transport;

// Spike 0(c): discover a panel, complete the handshake, put HELLO WORLD on the glass and
// measure USB CDC round-trip latency. The exit criterion is a median under 20 ms.
//
// Usage: Crowsnest.LinkSpike [COM4] [--pings 200] [--hold]

Console.OutputEncoding = System.Text.Encoding.UTF8;

string? requestedPort = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
int pingCount = ArgValue("--pings", 200);
bool hold = args.Contains("--hold", StringComparer.Ordinal);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

IReadOnlyList<string> ports = requestedPort is not null
    ? [requestedPort]
    : Probe();

if (ports.Count == 0)
{
    Console.Error.WriteLine("No serial ports found. Is the panel plugged in?");
    return 1;
}

foreach (string port in ports)
{
    Console.WriteLine($"probing {port} ...");

    var transport = new SerialPortTransport(port);
    var device = new PanelDeviceConnection(transport);
    device.LogReceived = log => Console.WriteLine($"  [fw {log.Level}] {log.Message}");

    try
    {
        await device.ConnectAsync(cts.Token);
    }
    catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"  not a panel: {e.Message}");
        await device.DisposeAsync();
        continue;
    }

    await using (device)
    {
        DeviceCapabilities caps = device.Capabilities!;
        Console.WriteLine($"  {device.Identity!.DeviceType} fw {device.Identity.FirmwareVersion} id {device.Identity.HardwareId}");
        Console.WriteLine($"  {caps.Width}x{caps.Height} {caps.Shape}, encoder {caps.HasEncoder} ({caps.DetentsPerClick}/click), touch {caps.HasTouch}");
        Console.WriteLine();

        Task inputs = Task.Run(
            async () =>
            {
                await foreach (DeviceInputEvent input in device.Inputs)
                {
                    Console.WriteLine($"  input {input}");
                }
            },
            CancellationToken.None);

        Console.WriteLine("HELLO WORLD ->");
        await device.RenderAsync(SelfTestFrames.HelloWorld(), cts.Token);
        await Task.Delay(1500, cts.Token);

        foreach (DisplayFrame frame in SelfTestFrames.All(firstRevision: 10).Skip(1))
        {
            await device.RenderAsync(frame, cts.Token);
            await Task.Delay(1500, cts.Token);
        }

        await MeasureAsync(device, pingCount, cts.Token);

        if (hold)
        {
            Console.WriteLine();
            Console.WriteLine("holding the link open — turn the knob, press it, tap the screen. Ctrl+C to stop.");
            await device.RenderAsync(SelfTestFrames.ComExample(revision: 100), cts.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop.
            }
        }

        await inputs.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    return 0;
}

Console.Error.WriteLine("No panel answered on any port.");
return 2;

static async Task MeasureAsync(PanelDeviceConnection device, int count, CancellationToken ct)
{
    // Warm the path: the first exchange pays for buffer allocation on both ends.
    for (int i = 0; i < 10; i++)
    {
        await device.MeasureRoundTripAsync(ct);
    }

    List<double> samples = new(count);
    var wall = Stopwatch.StartNew();

    for (int i = 0; i < count; i++)
    {
        samples.Add((await device.MeasureRoundTripAsync(ct)).TotalMilliseconds);
    }

    wall.Stop();
    samples.Sort();

    double median = samples[samples.Count / 2];
    double p95 = samples[(int)(samples.Count * 0.95)];

    Console.WriteLine();
    Console.WriteLine($"round trip over {count} pings in {wall.ElapsedMilliseconds} ms");
    Console.WriteLine($"  min {samples[0]:F2} ms   median {median:F2} ms   p95 {p95:F2} ms   max {samples[^1]:F2} ms");
    Console.WriteLine(median < 20
        ? "  spike 0(c) PASSES: median under the 20 ms budget."
        : "  spike 0(c) FAILS: median over the 20 ms budget.");
}

static IReadOnlyList<string> Probe()
{
    List<string> ports = [];
    foreach (DeviceCandidate candidate in DeviceDiscovery.Enumerate())
    {
        string marker = candidate.MatchesKnownBoard ? " <- known board" : string.Empty;
        Console.WriteLine($"  {candidate.PortName,-6} {candidate.Description}{marker}");
        ports.Add(candidate.PortName);
    }

    Console.WriteLine();
    return ports;
}

int ArgValue(string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
        ? value
        : fallback;
}
