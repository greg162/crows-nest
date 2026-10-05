using System.Globalization;
using System.Text;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Core.Panels;
using Crowsnest.Core.Panels.Com;
using Crowsnest.Device;
using Crowsnest.Device.Protocol;
using Crowsnest.Device.Transport;
using Crowsnest.Sim;
using Crowsnest.SimConnect;
using Microsoft.Extensions.Logging;

namespace Crowsnest.DeviceSimulator;

/// <summary>
/// The real host stack end to end: <see cref="PanelCoordinator"/> with the shipped registry and
/// pages, talking to a sim on one side and, through <see cref="DeviceConnection"/>, the wire
/// protocol and a loopback link, to a <see cref="SimulatedDevice"/> on the other.
///
/// The sim is either a <see cref="FakeParameterGateway"/>, so only the sim and the glass are
/// pretend, or with <c>--msfs</c> the real <see cref="SimConnectParameterGateway"/>, so only the
/// glass is (spec §13 phase 2: "driven by the device simulator").
/// </summary>
internal sealed class Demo : IAsyncDisposable
{
    private const string Esc = "\u001b";
    private const string EraseToEndOfLine = Esc + "[K";
    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");
    private static readonly ParameterId ComSpacing = new("com1.spacing");

    private readonly CancellationTokenSource _stop = new();
    private readonly SimulatedDevice _simulated;
    private readonly DeviceConnection _device;
    private readonly List<string> _log = [];
    private readonly int _logLines;
    private readonly Lock _gate = new();
    private readonly ISimParameterGateway _sim;
    private readonly SimConnectParameterGateway? _msfs;
    private Task? _simulatedLoop;
    private Task? _hostLoop;
    private Task? _simLoop;

    private Demo(bool msfs)
    {
        (LoopbackTransport hostEnd, LoopbackTransport deviceEnd) = LoopbackTransport.CreatePair();
        _simulated = new SimulatedDevice(deviceEnd);
        _device = new DeviceConnection(hostEnd);

        Setup = PanelCatalog.Load();
        if (msfs)
        {
            _msfs = new SimConnectParameterGateway(Setup.Registry, () => new ManagedSimConnectClient(), new NoteLogger(this));
            _sim = _msfs;
            _logLines = 12;
        }
        else
        {
            FakeParameterGateway fake = new(Setup);
            fake.On("COM_1_SPACING_MODE_SWITCH", () => ToggleComSpacing(fake));
            Fake = fake;
            _sim = Fake;
            _logLines = 6;
        }
    }

    public PanelSetup Setup { get; }

    /// <summary>Null with <c>--msfs</c>.</summary>
    public FakeParameterGateway? Fake { get; }

    public HostState? Latest { get; private set; }

    public Action? Changed { get; set; }

    public static async Task RunInteractiveAsync(bool msfs)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Interactive mode needs a console. Use --script instead.");
            return;
        }

        using IDisposable screen = AlternateScreen();
        await using Demo demo = await StartAsync(msfs);
        demo.Changed = () => demo.Draw(fullScreen: true);
        demo.Draw(fullScreen: true);

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
                    await demo._simulated.TurnEncoderAsync(fast);
                    break;
                case ConsoleKey.LeftArrow:
                    await demo._simulated.TurnEncoderAsync(-fast);
                    break;
                case ConsoleKey.Spacebar:
                    await demo._simulated.PressKnobAsync(held: false);
                    break;
                case ConsoleKey.Enter:
                    await demo._simulated.PressKnobAsync(held: true);
                    break;
                case ConsoleKey.T:
                    await demo._simulated.TapAsync(240, 240);
                    break;
                case ConsoleKey.S when demo.Fake is { } fake:
                    ToggleComSpacing(fake);
                    demo.Note("cockpit: spacing switch");
                    break;
                case ConsoleKey.S:
                    await demo._sim.InvokeAsync("COM_1_SPACING_MODE_SWITCH", 0, CancellationToken.None);
                    demo.Note("sent COM_1_SPACING_MODE_SWITCH");
                    break;
                case ConsoleKey.K when demo.Fake is { } fake:
                    fake.SetFromCockpit(ComStandby, fake.Value(ComStandby) + 25);
                    demo.Note("cockpit: standby knob +25 kHz");
                    break;
                case ConsoleKey.I when demo.Fake is { } fake:
                    fake.IgnoreWrites = !fake.IgnoreWrites;
                    demo.Note(fake.IgnoreWrites ? "sim: now ignoring writes" : "sim: accepting writes again");
                    break;
                case ConsoleKey.Q or ConsoleKey.Escape:
                    await demo._stop.CancelAsync();
                    break;
            }

            demo.Draw(fullScreen: true);
        }
    }

    /// <summary>A fixed tour of the behaviour against the fake sim, printing the device's screen after each step. Needs no keyboard.</summary>
    public static async Task RunScriptAsync()
    {
        await using Demo demo = await StartAsync(msfs: false);
        FakeParameterGateway fake = demo.Fake!;

        async Task Step(string what, Func<Task> act, int settleMs = 250)
        {
            Console.WriteLine($"── {what}");
            await act();
            await Task.Delay(settleMs);
            demo.Draw(fullScreen: false);
        }

        await Step("start: the sim reports COM 1", () => Task.CompletedTask, 400);
        await Step("short press: cursor to kHz", () => demo._simulated.PressKnobAsync().AsTask(), 100);
        await Step("turn +2, look at once: pending", () => demo._simulated.TurnEncoderAsync(2).AsTask(), 40);
        await Step("…a moment later: written and confirmed", () => Task.CompletedTask, 300);
        await Step("tap: swap", () => demo._simulated.TapAsync(240, 240).AsTask());
        await Step("cockpit spacing switch to 8.33, then turn +1", async () =>
        {
            ToggleComSpacing(fake);
            await Task.Delay(100);
            await demo._simulated.TurnEncoderAsync(1);
        }, 400);
        await Step("cockpit spacing switch back to 25 kHz: the sim snaps", () =>
        {
            ToggleComSpacing(fake);
            return Task.CompletedTask;
        }, 300);
        await Step("the sim stops accepting writes; turn +1 and wait out the settle timeout", async () =>
        {
            fake.IgnoreWrites = true;
            await demo._simulated.TurnEncoderAsync(1);
        }, 1_900);
        await Step("someone turns the cockpit knob", () =>
        {
            fake.IgnoreWrites = false;
            fake.SetFromCockpit(ComStandby, 121_500);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The cockpit's COM 1 spacing switch in the fake sim. Going back to 25 kHz snaps both COM 1
    /// values, as all three aircraft in the matrix did: 118.505 → 118.500, 119.005 → 119.000.
    /// </summary>
    private static void ToggleComSpacing(FakeParameterGateway fake)
    {
        int mode = fake.Value(ComSpacing) == 0 ? 1 : 0;
        fake.SetFromCockpit(ComSpacing, mode);

        if (mode == 0)
        {
            ComChannelGrid grid = new(ChannelSpacing.TwentyFiveKhz);
            fake.SetFromCockpit(ComActive, grid.Snap(fake.Value(ComActive)));
            fake.SetFromCockpit(ComStandby, grid.Snap(fake.Value(ComStandby)));
        }
    }

    private static async Task<Demo> StartAsync(bool msfs)
    {
        Demo demo = new(msfs);
        demo._simulated.Rendered = state =>
        {
            demo.Latest = state;
            demo.Changed?.Invoke();
        };
        demo._simulatedLoop = demo._simulated.RunAsync(demo._stop.Token);

        await demo._device.ConnectAsync(demo._stop.Token);

        if (demo._msfs is { } gateway)
        {
            demo._simLoop = gateway.RunAsync(demo._stop.Token);
        }

        PanelCoordinator coordinator = new(
            demo.Setup, demo._sim, DefaultInputActionMap.Instance,
            TuningOptions.Default, TimeProvider.System, e => demo.Note($"effect failed: {e.Message}"));
        demo._hostLoop = Task.WhenAll(
            coordinator.RunAsync(demo._stop.Token),
            coordinator.RunDeviceAsync(demo._device.Identity!.HardwareId, demo._device, demo._stop.Token));

        return demo;
    }

    private void Note(string line)
    {
        lock (_gate)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            if (_log.Count > _logLines)
            {
                _log.RemoveAt(0);
            }
        }

        Changed?.Invoke();
    }

    /// <param name="fullScreen">
    /// Redraw the whole screen in place (interactive). Otherwise append the device's screen, for --script.
    /// </param>
    private void Draw(bool fullScreen)
    {
        lock (_gate)
        {
            StringBuilder screen = new();
            if (Latest is { } state)
            {
                screen.Append(ConsoleDeviceRenderer.Render(state));
            }

            if (Fake is { } fake)
            {
                string spacing = fake.Value(ComSpacing) == 1 ? "8.33" : "25";
                screen.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    in the sim: COM 1 {Mhz(fake.Value(ComActive))} / stby {Mhz(fake.Value(ComStandby))}, {spacing} kHz{(fake.IgnoreWrites ? ", IGNORING WRITES" : "")}"));
            }
            else
            {
                screen.AppendLine("    sim: MSFS over SimConnect");
            }

            if (fullScreen)
            {
                screen.AppendLine();
                screen.AppendLine("    ←/→ turn (shift ×5)   space cursor   enter next page   t swap   q quit");
                screen.AppendLine(Fake is null
                    ? "    s send the spacing switch to the sim"
                    : "    s cockpit spacing switch   k cockpit knob   i sim ignores writes");
                screen.AppendLine();
                foreach (string line in _log)
                {
                    screen.Append("    ").AppendLine(line);
                }

                // Home, then every line erased to its end, then everything below erased: one
                // write, no Console.Clear, so terminals (VS Code's especially) neither flicker
                // nor push half-drawn frames into the scrollback.
                string body = screen.ToString().ReplaceLineEndings(EraseToEndOfLine + Environment.NewLine);
                Console.Out.Write(Esc + "[H" + body + Esc + "[J");
                Console.Out.Flush();
            }
            else
            {
                Console.WriteLine(screen.ToString());
            }
        }
    }

    /// <summary>Switches to the terminal's alternate screen, as full-screen console apps do, and back on dispose.</summary>
    private static IDisposable AlternateScreen()
    {
        Console.Out.Write(Esc + "[?1049h" + Esc + "[?25l" + Esc + "[2J");
        return new Restore(() => Console.Out.Write(Esc + "[?25h" + Esc + "[?1049l"));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static string Mhz(int khz) => (khz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(new[] { _hostLoop, _simLoop, _simulatedLoop }.OfType<Task>().Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        await _device.DisposeAsync();
        await _simulated.DisposeAsync();
        await _sim.DisposeAsync();
        _stop.Dispose();
    }

    /// <summary>Gateway logs into the demo's log area. Debug and below are dropped.</summary>
    private sealed class NoteLogger(Demo demo) : ILogger<SimConnectParameterGateway>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                demo.Note(logLevel >= LogLevel.Warning ? $"{logLevel}: {formatter(state, exception)}" : formatter(state, exception));
            }
        }
    }
}
