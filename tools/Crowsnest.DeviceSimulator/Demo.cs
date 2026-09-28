using System.Globalization;
using Crowsnest.Core.Application;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;
using Crowsnest.Device;
using Crowsnest.Device.Protocol;
using Crowsnest.Device.Transport;
using Crowsnest.Sim;

namespace Crowsnest.DeviceSimulator;

/// <summary>
/// The real host stack end to end: <see cref="PanelCoordinator"/> with the shipped registry and
/// pages, talking to a <see cref="FakeParameterGateway"/> on one side and, through
/// <see cref="PanelDeviceConnection"/>, the wire protocol and a loopback link, to a
/// <see cref="SimulatedPanel"/> on the other. Only the sim and the glass are pretend.
/// </summary>
internal sealed class Demo : IAsyncDisposable
{
    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");

    private readonly CancellationTokenSource _stop = new();
    private readonly SimulatedPanel _panel;
    private readonly PanelDeviceConnection _device;
    private readonly List<string> _log = [];
    private readonly Lock _gate = new();
    private Task? _panelLoop;
    private Task? _hostLoop;

    private Demo()
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        _panel = new SimulatedPanel(deviceEnd);
        _device = new PanelDeviceConnection(hostEnd);
        Sim = new FakeParameterGateway();
    }

    public FakeParameterGateway Sim { get; }

    public HostState? Latest { get; private set; }

    public Action? Changed { get; set; }

    public static async Task RunInteractiveAsync()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Interactive mode needs a console. Use --script instead.");
            return;
        }

        await using Demo demo = await StartAsync();
        demo.Changed = () => demo.Draw(clear: true);
        demo.Draw(clear: true);

        while (!demo._stop.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(15);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            int fast = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 5 : 1;

            switch (key.Key)
            {
                case ConsoleKey.RightArrow:
                    await demo._panel.TurnEncoderAsync(fast);
                    break;
                case ConsoleKey.LeftArrow:
                    await demo._panel.TurnEncoderAsync(-fast);
                    break;
                case ConsoleKey.Spacebar:
                    await demo._panel.PressKnobAsync(held: false);
                    break;
                case ConsoleKey.Enter:
                    await demo._panel.PressKnobAsync(held: true);
                    break;
                case ConsoleKey.T:
                    await demo._panel.TapAsync(240, 240);
                    break;
                case ConsoleKey.S:
                    demo.Sim.ToggleSpacing();
                    demo.Note("cockpit: spacing switch");
                    break;
                case ConsoleKey.K:
                    demo.Sim.SetFromCockpit(ComStandby, demo.Sim.Value(ComStandby) + 25);
                    demo.Note("cockpit: standby knob +25 kHz");
                    break;
                case ConsoleKey.I:
                    demo.Sim.IgnoreWrites = !demo.Sim.IgnoreWrites;
                    demo.Note(demo.Sim.IgnoreWrites ? "sim: now ignoring writes" : "sim: accepting writes again");
                    break;
                case ConsoleKey.Q or ConsoleKey.Escape:
                    await demo._stop.CancelAsync();
                    break;
            }

            demo.Draw(clear: true);
        }
    }

    /// <summary>A fixed tour of the behaviour, printing the panel after each step. Needs no keyboard.</summary>
    public static async Task RunScriptAsync()
    {
        await using Demo demo = await StartAsync();

        async Task Step(string what, Func<Task> act, int settleMs = 250)
        {
            Console.WriteLine($"── {what}");
            await act();
            await Task.Delay(settleMs);
            demo.Draw(clear: false);
        }

        await Step("start: the sim reports COM 1", () => Task.CompletedTask, 400);
        await Step("short press: cursor to kHz", () => demo._panel.PressKnobAsync().AsTask(), 100);
        await Step("turn +2, look at once: pending", () => demo._panel.TurnEncoderAsync(2).AsTask(), 40);
        await Step("…a moment later: written and confirmed", () => Task.CompletedTask, 300);
        await Step("tap: swap", () => demo._panel.TapAsync(240, 240).AsTask());
        await Step("cockpit spacing switch to 8.33, then turn +1", async () =>
        {
            demo.Sim.ToggleSpacing();
            await Task.Delay(100);
            await demo._panel.TurnEncoderAsync(1);
        }, 400);
        await Step("cockpit spacing switch back to 25 kHz: the sim snaps", () =>
        {
            demo.Sim.ToggleSpacing();
            return Task.CompletedTask;
        }, 300);
        await Step("the sim stops accepting writes; turn +1 and wait out the settle timeout", async () =>
        {
            demo.Sim.IgnoreWrites = true;
            await demo._panel.TurnEncoderAsync(1);
        }, 1_900);
        await Step("someone turns the cockpit knob", () =>
        {
            demo.Sim.IgnoreWrites = false;
            demo.Sim.SetFromCockpit(ComStandby, 121_500);
            return Task.CompletedTask;
        });
    }

    private static async Task<Demo> StartAsync()
    {
        Demo demo = new();
        demo._panel.Rendered = state =>
        {
            demo.Latest = state;
            demo.Changed?.Invoke();
        };
        demo._panelLoop = demo._panel.RunAsync(demo._stop.Token);

        await demo._device.ConnectAsync(demo._stop.Token);

        PanelCoordinator coordinator = new(
            DefaultParameters.Load(), demo.Sim, demo._device, DefaultInputActionMap.Instance,
            TuningOptions.Default, TimeProvider.System, e => demo.Note($"effect failed: {e.Message}"));
        demo._hostLoop = coordinator.RunAsync(demo._stop.Token);

        return demo;
    }

    private void Note(string line)
    {
        lock (_gate)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            if (_log.Count > 6)
            {
                _log.RemoveAt(0);
            }
        }
    }

    private void Draw(bool clear)
    {
        lock (_gate)
        {
            if (clear && !Console.IsOutputRedirected)
            {
                Console.Clear();
            }

            if (Latest is { } state)
            {
                Console.Write(ConsolePanelRenderer.Render(state));
            }

            string spacing = Sim.Value(new ParameterId("com1.spacing")) == 1 ? "8.33" : "25";
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    in the sim: COM 1 {Mhz(Sim.Value(ComActive))} / stby {Mhz(Sim.Value(ComStandby))}, {spacing} kHz{(Sim.IgnoreWrites ? ", IGNORING WRITES" : "")}"));

            if (clear)
            {
                Console.WriteLine();
                Console.WriteLine("    ←/→ turn (shift ×5)   space cursor   enter next page   t swap");
                Console.WriteLine("    s cockpit spacing switch   k cockpit knob   i sim ignores writes   q quit");
                Console.WriteLine();
                foreach (string line in _log)
                {
                    Console.WriteLine($"    {line}");
                }
            }

            Console.WriteLine();
        }
    }

    private static string Mhz(int khz) => (khz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(new[] { _hostLoop, _panelLoop }.OfType<Task>().Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        await _device.DisposeAsync();
        await _panel.DisposeAsync();
        await Sim.DisposeAsync();
        _stop.Dispose();
    }
}
