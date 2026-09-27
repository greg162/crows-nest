using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

// Spike 0(a): open a SimConnect session under .NET 10, read COM ACTIVE FREQUENCY:1,
// COM STANDBY FREQUENCY:1 and COM SPACING MODE:1, write them with the standard radio events
// and watch them land.
//
// Waits for the SimStart system event, then for CAMERA STATE to reach a flying view (the pilot
// has clicked "Start Flight") and 3 s of quiet, writing nothing meanwhile, because an
// aircraft's initialisation overwrites values during flight load and right up to the handover
// (spec s5.3). SimStart alone is too early in MSFS 2024, and pause flags do not change at the
// Start Flight screen. Start it at the main menu; g skips whichever wait it is in. Then runs a
// self-test: write standby, swap, swap back, toggle spacing, toggle back,
// restore standby, and hold for 30 s to catch late overwrites. It prints a one-line SUMMARY
// for the aircraft matrix, then takes commands until Ctrl+C:
//   s 122.800   set standby     x   swap active/standby     m   toggle 25 / 8.33 kHz     q   quit
//
// Every line is stamped with seconds since launch.

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);

SimConnect sim;
try
{
    sim = new SimConnect("Crowsnest SimSpike", IntPtr.Zero, 0, signal, 0);
}
catch (COMException e)
{
    Console.Error.WriteLine($"SimConnect_Open failed (0x{e.HResult:X8}). Is the sim running?");
    return 1;
}

using (sim)
{
    var session = new Session(sim);
    session.Wire();

    var commands = new ConcurrentQueue<string>();
    var stdin = new Thread(() =>
    {
        while (Console.ReadLine() is { } line)
        {
            commands.Enqueue(line.Trim());
        }
    })
    { IsBackground = true };
    stdin.Start();

    while (!cts.IsCancellationRequested && !session.Quit)
    {
        if (signal.WaitOne(50))
        {
            sim.ReceiveMessage();
        }

        session.Tick();

        while (commands.TryDequeue(out string? command))
        {
            if (command == "q")
            {
                cts.Cancel();
            }
            else
            {
                session.Command(command);
            }
        }
    }

    session.PrintSummaryIfMissing();
}

return 0;

internal enum Definition
{
    Com1,
    Title,
    Camera,
}

internal enum Request
{
    Com1,
    Title,
    Camera,
    SimState,
    AircraftLoaded,
}

internal enum ClientEvent
{
    Com1StandbySetHz,
    Com1Swap,
    Com1SpacingToggle,
}

// Shares the event id space with ClientEvent, so it starts well clear of it.
internal enum SystemEvent
{
    SimStart = 100,
    PauseEx1,
    SimStop,
}

internal enum Group
{
    Radios,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Com1Data
{
    public double ActiveHz;
    public double StandbyHz;
    public double SpacingMode; // 0 = 25 kHz, 1 = 8.33 kHz
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CameraData
{
    public double State;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct TitleData
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;
}

internal sealed class Session(SimConnect sim)
{
    private const uint UserObject = 0; // SIMCONNECT_OBJECT_ID_USER
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(3);

    // Seen in the MSFS 2024 C185: 2 cockpit, 3 external, 4 showcase. Not flying: 12 aircraft
    // selection, 16 Start Flight screen, 30 fly-in, 32 and 35 menus and loading.
    private static readonly HashSet<int> FlyingCameras = [2, 3, 4];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private Phase _phase = Phase.WaitingForSimStart;
    private Com1Data? _last;
    private string _title = "";
    private string _aircraftPath = "";

    private double? _simStartAt;
    private bool _manualStart;
    private double? _lastChangeBeforeStart;
    private int _changesBeforeStart;
    private readonly Stopwatch _settleClock = new();
    private double? _lastChangeAfterStart; // seconds after SimStart
    private int _changesAfterStart;
    private string _defaultSpacing = "?";
    private double? _flyingAt; // seconds after SimStart
    private double? _cameraState;
    private readonly List<string> _handover = []; // pause and camera changes after SimStart

    private List<Step> _steps = [];
    private int _stepIndex;
    private readonly Stopwatch _stepClock = new();
    private readonly List<(string Name, bool Ok, double Ms)> _results = [];

    private readonly Stopwatch _holdClock = new();
    private readonly List<double> _holdChanges = [];

    private readonly Stopwatch _writeClock = new();

    public bool Quit { get; private set; }

    private enum Phase
    {
        WaitingForSimStart,
        Settling,
        SelfTest,
        Hold,
        Interactive,
    }

    private sealed record Step(string Name, Action Act, Func<Com1Data, bool> Done);

    public void Wire()
    {
        sim.OnRecvOpen += (_, data) =>
            Log($"connected: {data.szApplicationName} {data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor} build {data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}, SimConnect {data.dwSimConnectVersionMajor}.{data.dwSimConnectVersionMinor}");

        sim.OnRecvQuit += (_, _) =>
        {
            Log("sim quit");
            Quit = true;
        };

        sim.OnRecvException += (_, data) =>
            Log($"  SIMCONNECT EXCEPTION {(SIMCONNECT_EXCEPTION)data.dwException} (send id {data.dwSendID}, index {data.dwIndex})");

        sim.OnRecvSimobjectData += (_, data) =>
        {
            switch ((Request)data.dwRequestID)
            {
                case Request.Com1:
                    OnCom1((Com1Data)data.dwData[0]);
                    break;
                case Request.Camera:
                    double state = ((CameraData)data.dwData[0]).State;
                    Log($"camera state {state:F0}{(_cameraState is { } was ? $" (was {was:F0})" : "")}");
                    _cameraState = state;
                    if (_flyingAt is null && _simStartAt is { } started && FlyingCameras.Contains((int)state))
                    {
                        _flyingAt = Clock.Elapsed.TotalSeconds - started;
                        Log($"  flying camera +{_flyingAt:F1} s after SimStart");
                    }

                    NoteHandover($"camera {state:F0}");
                    break;
                case Request.Title:
                    _title = ((TitleData)data.dwData[0]).Title;
                    Log($"aircraft title: {_title}");
                    break;
            }
        };

        sim.OnRecvSystemState += (_, data) =>
        {
            switch ((Request)data.dwRequestID)
            {
                case Request.SimState:
                    // MSFS 2024 reports "running" at the main menu too, so this is logged, not acted on.
                    Log($"sim state: {(data.dwInteger == 1 ? "running" : "not running")} (ignored: MSFS 2024 says running at the menu)");
                    break;
                case Request.AircraftLoaded:
                    _aircraftPath = data.szString;
                    Log($"aircraft loaded: {_aircraftPath}");
                    break;
            }
        };

        sim.OnRecvEvent += (_, data) =>
        {
            if (data.uEventID == (uint)SystemEvent.PauseEx1)
            {
                // Flags: 1 full pause, 2 legacy pause, 4 active pause, 8 sim paused, others running.
                Log($"pause flags {data.dwData}");
                NoteHandover($"pause {data.dwData}");
            }
            else if (data.uEventID == (uint)SystemEvent.SimStop)
            {
                Log("SimStop");
            }
            else if (data.uEventID == (uint)SystemEvent.SimStart)
            {
                if (_simStartAt is null)
                {
                    OnSimStart();
                }
                else
                {
                    Log("SimStart fired again (returning from a menu?)");
                }
            }
        };

        sim.AddToDataDefinition(Definition.Com1, "COM ACTIVE FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(Definition.Com1, "COM STANDBY FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(Definition.Com1, "COM SPACING MODE:1", "Enum", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Com1Data>(Definition.Com1);

        sim.AddToDataDefinition(Definition.Title, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<TitleData>(Definition.Title);

        sim.MapClientEventToSimEvent(ClientEvent.Com1StandbySetHz, "COM_STBY_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(ClientEvent.Com1Swap, "COM_STBY_RADIO_SWAP");
        sim.MapClientEventToSimEvent(ClientEvent.Com1SpacingToggle, "COM_1_SPACING_MODE_SWITCH");
        sim.AddClientEventToNotificationGroup(Group.Radios, ClientEvent.Com1StandbySetHz, false);
        sim.AddClientEventToNotificationGroup(Group.Radios, ClientEvent.Com1Swap, false);
        sim.AddClientEventToNotificationGroup(Group.Radios, ClientEvent.Com1SpacingToggle, false);
        sim.SetNotificationGroupPriority(Group.Radios, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);

        sim.AddToDataDefinition(Definition.Camera, "CAMERA STATE", "Enum", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<CameraData>(Definition.Camera);

        sim.SubscribeToSystemEvent(SystemEvent.SimStart, "SimStart");
        sim.SubscribeToSystemEvent(SystemEvent.PauseEx1, "Pause_EX1");
        sim.SubscribeToSystemEvent(SystemEvent.SimStop, "SimStop");

        sim.RequestSystemState(Request.SimState, "Sim");
        sim.RequestSystemState(Request.AircraftLoaded, "AircraftLoaded");

        // Every visual frame, but only when a value actually changed.
        sim.RequestDataOnSimObject(
            Request.Com1,
            Definition.Com1,
            UserObject,
            SIMCONNECT_PERIOD.VISUAL_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
            0,
            0,
            0);
        sim.RequestDataOnSimObject(Request.Camera, Definition.Camera, UserObject, SIMCONNECT_PERIOD.VISUAL_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

        Log("waiting for SimStart. If the flight is already running, type g to start by hand");
    }

    public void Tick()
    {
        switch (_phase)
        {
            // The current camera, not the first flying one seen: camera 2 can flicker for 80 ms
            // on its way from 16 to 3.
            case Phase.Settling when _cameraState is { } camera && FlyingCameras.Contains((int)camera) && _settleClock.Elapsed > QuietTime:
                FinishSettling();
                break;

            case Phase.SelfTest when _stepClock.Elapsed > StepTimeout:
                Step step = _steps[_stepIndex];
                Log($"  self-test {_stepIndex + 1}/{_steps.Count} {step.Name}: FAILED, no matching value within {StepTimeout.TotalSeconds:F0} s");
                _results.Add((step.Name, false, 0));
                _stepIndex++;
                RunStep();
                break;

            case Phase.Hold when _holdClock.Elapsed > HoldTime:
                Log(_holdChanges.Count == 0
                    ? $"  hold: nothing changed in {HoldTime.TotalSeconds:F0} s"
                    : $"  hold: {_holdChanges.Count} change(s) nobody asked for. The aircraft is still initialising after SimStart.");
                _phase = Phase.Interactive;
                PrintSummary();
                PrintHelp();
                break;
        }
    }

    public void Command(string command)
    {
        if (_phase == Phase.WaitingForSimStart && command == "g")
        {
            _manualStart = true;
            OnSimStart();
            return;
        }

        if (_phase == Phase.Settling && command == "g")
        {
            Log("  settle cut short by hand");
            FinishSettling();
            return;
        }

        if (_phase != Phase.Interactive)
        {
            Log("  (self-test still running; commands are taken once it finishes)");
            return;
        }

        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["s", string mhz] when decimal.TryParse(mhz, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value):
                WriteStandby((uint)Math.Round(value * 1_000_000m));
                Log($"  -> standby {Mhz((double)value * 1_000_000)}");
                break;
            case ["x"]:
                _writeClock.Restart();
                Transmit(ClientEvent.Com1Swap);
                Log("  -> swap");
                break;
            case ["m"]:
                _writeClock.Restart();
                Transmit(ClientEvent.Com1SpacingToggle);
                Log("  -> toggle spacing mode");
                break;
            case []:
                break;
            default:
                PrintHelp();
                break;
        }
    }

    // At quit, so an aborted run still leaves a row for the matrix.
    public void PrintSummaryIfMissing()
    {
        if (_phase != Phase.Interactive)
        {
            PrintSummary();
        }
    }

    private void PrintSummary()
    {
        string aircraft = _title.Length > 0 ? _title : _aircraftPath.Length > 0 ? _aircraftPath : "unknown aircraft";
        string start = _simStartAt is { } s
            ? _manualStart ? $"started by hand at +{s:F1} s" : $"SimStart +{s:F1} s"
            : "SimStart never seen";
        string load = _changesBeforeStart == 0
            ? "no changes before start"
            : $"{_changesBeforeStart} change(s) before start, last +{_lastChangeBeforeStart:F1} s";
        string settle = _phase is Phase.WaitingForSimStart or Phase.Settling
            ? "settle not finished"
            : _changesAfterStart == 0
                ? "no changes after start"
                : $"{_changesAfterStart} change(s) after start, last +{_lastChangeAfterStart:F1} s after";
        string flying = _flyingAt is { } f ? $"flying camera +{f:F1} s after start" : "flying camera never seen";
        string handover = _handover.Count == 0 ? "no pause/camera changes after start" : string.Join(", ", _handover);
        string hold = _phase switch
        {
            Phase.Interactive when _holdChanges.Count == 0 => "hold stable",
            Phase.Interactive => $"hold CHANGED at +{string.Join(", +", _holdChanges.Select(t => t.ToString("F1", CultureInfo.InvariantCulture)))} s after SimStart",
            _ => "hold not reached",
        };
        string steps = _results.Count == 0
            ? "self-test not run"
            : string.Join(" | ", _results.Select(r => r.Ok ? $"{r.Name} ok {r.Ms:F0} ms" : $"{r.Name} FAILED"));

        Log($"SUMMARY | {aircraft} | {start} | {load} | {settle} | {flying} | {handover} | settled spacing {_defaultSpacing} kHz | {steps} | {hold}");
    }

    private void OnSimStart()
    {
        _simStartAt = Clock.Elapsed.TotalSeconds;
        Log($"{(_manualStart ? "started by hand" : "SimStart")}. Waiting for a flying camera, then {QuietTime.TotalSeconds:F0} s of quiet (g to cut short)");
        sim.RequestSystemState(Request.AircraftLoaded, "AircraftLoaded");
        sim.RequestDataOnSimObject(Request.Title, Definition.Title, UserObject, SIMCONNECT_PERIOD.ONCE, 0, 0, 0, 0);
        _phase = Phase.Settling;
        _settleClock.Restart();
    }

    private void OnCom1(Com1Data data)
    {
        _last = data;
        string spacing = SpacingOf(data);
        // Raw Hz as well: the sim stores 8.33 channels by name (118005000), not true frequency.
        Log($"COM1  active {Mhz(data.ActiveHz)}   standby {Mhz(data.StandbyHz)}   raw standby {data.StandbyHz:F0} Hz   spacing {spacing} kHz");

        switch (_phase)
        {
            case Phase.WaitingForSimStart:
                _changesBeforeStart++;
                _lastChangeBeforeStart = Clock.Elapsed.TotalSeconds;
                break;

            case Phase.Settling:
                _changesAfterStart++;
                _lastChangeAfterStart = Clock.Elapsed.TotalSeconds - _simStartAt!.Value;
                Log($"  settle: changed +{_lastChangeAfterStart:F1} s after SimStart with no write from us");
                _settleClock.Restart();
                break;

            case Phase.SelfTest when _steps[_stepIndex].Done(data):
                double ms = _stepClock.Elapsed.TotalMilliseconds;
                Step step = _steps[_stepIndex];
                Log($"  self-test {_stepIndex + 1}/{_steps.Count} {step.Name}: ok after {ms:F1} ms");
                _results.Add((step.Name, true, ms));
                _stepIndex++;
                RunStep();
                break;

            case Phase.Hold:
                double sinceStart = Clock.Elapsed.TotalSeconds - _simStartAt!.Value;
                _holdChanges.Add(sinceStart);
                Log($"  hold: CHANGED +{sinceStart:F1} s after SimStart with no write from us");
                break;

            case Phase.Interactive when _writeClock.IsRunning:
                Log($"  ({_writeClock.Elapsed.TotalMilliseconds:F1} ms after last write)");
                _writeClock.Stop();
                break;
        }
    }

    private void NoteHandover(string what)
    {
        if (_simStartAt is { } start && _phase != Phase.WaitingForSimStart)
        {
            _handover.Add($"{what} at +{Clock.Elapsed.TotalSeconds - start:F1} s");
        }

        if (_phase == Phase.Settling)
        {
            _settleClock.Restart();
        }
    }

    private void FinishSettling()
    {
        Log(_changesAfterStart == 0
            ? "  settle: COM1 never changed after SimStart"
            : $"  settle: {_changesAfterStart} COM1 change(s) after SimStart, last +{_lastChangeAfterStart:F1} s");
        TryStartSelfTest();
    }

    private void TryStartSelfTest()
    {
        if (_last is not { } orig)
        {
            Log("  no COM1 data yet; cannot run the self-test");
            _phase = Phase.Interactive;
            PrintHelp();
            return;
        }

        _defaultSpacing = SpacingOf(orig);
        uint active = (uint)Math.Round(orig.ActiveHz);
        uint standby = (uint)Math.Round(orig.StandbyHz);
        // Legal in both spacings, and different from both radios so every step is visible.
        uint target = new[] { 121_500_000u, 122_800_000u, 123_450_000u }
            .First(hz => !Matches(active, hz) && !Matches(standby, hz));
        bool was833 = orig.SpacingMode >= 0.5;

        _steps =
        [
            new("write", () => WriteStandby(target), d => Matches(d.StandbyHz, target)),
            new("swap", () => Transmit(ClientEvent.Com1Swap), d => Matches(d.ActiveHz, target) && Matches(d.StandbyHz, active)),
            new("swap back", () => Transmit(ClientEvent.Com1Swap), d => Matches(d.ActiveHz, active) && Matches(d.StandbyHz, target)),
            new("toggle", () => Transmit(ClientEvent.Com1SpacingToggle), d => d.SpacingMode >= 0.5 != was833),
            new("toggle back", () => Transmit(ClientEvent.Com1SpacingToggle), d => d.SpacingMode >= 0.5 == was833),
            new("restore", () => WriteStandby(standby), d => Matches(d.StandbyHz, standby)),
        ];

        Log($"self-test: target {Mhz(target)}, will restore standby {Mhz(standby)} and spacing {_defaultSpacing} kHz");
        _phase = Phase.SelfTest;
        _stepIndex = 0;
        RunStep();
    }

    private void RunStep()
    {
        if (_stepIndex == _steps.Count)
        {
            Log($"  self-test done. Holding {HoldTime.TotalSeconds:F0} s to catch anything the aircraft overwrites");
            _phase = Phase.Hold;
            _holdClock.Restart();
            return;
        }

        _stepClock.Restart();
        _steps[_stepIndex].Act();
    }

    private void WriteStandby(uint hz)
    {
        _writeClock.Restart();
        sim.TransmitClientEvent(UserObject, ClientEvent.Com1StandbySetHz, hz, Group.Radios, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    private void Transmit(ClientEvent e) =>
        sim.TransmitClientEvent(UserObject, e, 0, Group.Radios, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

    private static string SpacingOf(Com1Data d) => d.SpacingMode >= 0.5 ? "8.33" : "25";

    private static bool Matches(double hz, uint target) => Math.Abs(hz - target) < 500;

    private static string Mhz(double hz) => (hz / 1_000_000).ToString("F3", CultureInfo.InvariantCulture);

    private static void Log(string line) =>
        Console.WriteLine($"{Clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),9}  {line}");

    private static void PrintHelp() =>
        Log("commands: s 122.800 (set standby)   x (swap)   m (toggle 25 / 8.33 kHz)   q (quit). Turn the radio in the cockpit too; changes print here.");
}
