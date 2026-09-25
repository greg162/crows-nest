using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

// Spike 0(a): open a SimConnect session under .NET 10, read COM ACTIVE FREQUENCY:1 and
// COM STANDBY FREQUENCY:1, write COM_STBY_RADIO_SET_HZ and watch it land.
//
// Runs a self-test first (write a standby frequency, time the read-back, restore the
// original), then takes commands until Ctrl+C:
//   s 122.800   set standby     x   swap active/standby     q   quit

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
    Console.Error.WriteLine($"SimConnect_Open failed (0x{e.HResult:X8}). Is the sim running with a flight loaded?");
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
}

return 0;

internal enum Definition
{
    Com1,
}

internal enum Request
{
    Com1,
}

internal enum ClientEvent
{
    Com1StandbySetHz,
    Com1Swap,
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
}

internal sealed class Session(SimConnect sim)
{
    private const uint UserObject = 0; // SIMCONNECT_OBJECT_ID_USER

    private SelfTest _test = SelfTest.WaitingForFirstRead;
    private uint _original;
    private uint _target;
    private readonly Stopwatch _writeClock = new();
    private readonly List<double> _samples = [];

    public bool Quit { get; private set; }

    private enum SelfTest
    {
        WaitingForFirstRead,
        WaitingForTarget,
        WaitingForRestore,
        Done,
    }

    public void Wire()
    {
        sim.OnRecvOpen += (_, data) =>
            Console.WriteLine($"connected: {data.szApplicationName} {data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor} build {data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}, SimConnect {data.dwSimConnectVersionMajor}.{data.dwSimConnectVersionMinor}");

        sim.OnRecvQuit += (_, _) =>
        {
            Console.WriteLine("sim quit");
            Quit = true;
        };

        sim.OnRecvException += (_, data) =>
            Console.WriteLine($"  SIMCONNECT EXCEPTION {(SIMCONNECT_EXCEPTION)data.dwException} (send id {data.dwSendID}, index {data.dwIndex})");

        sim.OnRecvSimobjectData += (_, data) =>
        {
            if ((Request)data.dwRequestID == Request.Com1)
            {
                OnCom1((Com1Data)data.dwData[0]);
            }
        };

        sim.AddToDataDefinition(Definition.Com1, "COM ACTIVE FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(Definition.Com1, "COM STANDBY FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Com1Data>(Definition.Com1);

        sim.MapClientEventToSimEvent(ClientEvent.Com1StandbySetHz, "COM_STBY_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(ClientEvent.Com1Swap, "COM_STBY_RADIO_SWAP");
        sim.AddClientEventToNotificationGroup(Group.Radios, ClientEvent.Com1StandbySetHz, false);
        sim.AddClientEventToNotificationGroup(Group.Radios, ClientEvent.Com1Swap, false);
        sim.SetNotificationGroupPriority(Group.Radios, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);

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
    }

    public void Tick()
    {
        if (_test is SelfTest.WaitingForTarget or SelfTest.WaitingForRestore && _writeClock.Elapsed > TimeSpan.FromSeconds(3))
        {
            Console.WriteLine($"  self-test FAILED: standby never reached {Mhz(_target)} within 3 s. The aircraft may ignore standard radio events (spec s14).");
            _test = SelfTest.Done;
            PrintHelp();
        }
    }

    public void Command(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["s", string mhz] when decimal.TryParse(mhz, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value):
                WriteStandby((uint)Math.Round(value * 1_000_000m));
                break;
            case ["x"]:
                sim.TransmitClientEvent(UserObject, ClientEvent.Com1Swap, 0, Group.Radios, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                Console.WriteLine("  -> swap");
                break;
            case []:
                break;
            default:
                PrintHelp();
                break;
        }
    }

    private void OnCom1(Com1Data data)
    {
        double ms = _writeClock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"COM1  active {Mhz(data.ActiveHz)}   standby {Mhz(data.StandbyHz)}");

        switch (_test)
        {
            case SelfTest.WaitingForFirstRead:
                _original = (uint)Math.Round(data.StandbyHz);
                // Pick a target that is guaranteed to differ from what is there now.
                _target = _original == 121_500_000 ? 122_800_000u : 121_500_000u;
                Console.WriteLine($"  self-test: writing standby {Mhz(_target)}");
                _test = SelfTest.WaitingForTarget;
                WriteStandby(_target);
                break;

            case SelfTest.WaitingForTarget when Matches(data.StandbyHz, _target):
                _samples.Add(ms);
                Console.WriteLine($"  read back {Mhz(_target)} after {ms:F1} ms. Restoring {Mhz(_original)}");
                _test = SelfTest.WaitingForRestore;
                WriteStandby(_original);
                break;

            case SelfTest.WaitingForRestore when Matches(data.StandbyHz, _original):
                _samples.Add(ms);
                Console.WriteLine($"  read back {Mhz(_original)} after {ms:F1} ms.");
                Console.WriteLine($"  spike 0(a) PASSES: read, write and read-back all work. Write-to-read-back {_samples.Min():F1}-{_samples.Max():F1} ms.");
                _test = SelfTest.Done;
                PrintHelp();
                break;

            default:
                if (_writeClock.IsRunning && _test == SelfTest.Done)
                {
                    Console.WriteLine($"  ({ms:F1} ms after last write)");
                    _writeClock.Stop();
                }

                break;
        }
    }

    private void WriteStandby(uint hz)
    {
        _writeClock.Restart();
        sim.TransmitClientEvent(UserObject, ClientEvent.Com1StandbySetHz, hz, Group.Radios, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        if (_test == SelfTest.Done)
        {
            Console.WriteLine($"  -> standby {Mhz(hz)}");
        }
    }

    private static bool Matches(double hz, uint target) => Math.Abs(hz - target) < 500;

    private static string Mhz(double hz) => (hz / 1_000_000).ToString("F3", CultureInfo.InvariantCulture);

    private static void PrintHelp() =>
        Console.WriteLine("commands: s 122.800 (set standby)   x (swap)   q (quit). Turn the radio in the cockpit too; changes print here.");
}
