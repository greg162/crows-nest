using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;
using Sdk = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crowsnest.SimConnect;

/// <summary>
/// <see cref="ISimConnectClient"/> over the SDK's managed wrapper (spec §7.1's "insurance
/// policy", promoted to first because spike 0(a) proved it under .NET 10).
///
/// Everything touching the SimConnect object runs on one dedicated thread (spec §7.2: never
/// park a pool thread on the dispatch poll). That thread waits on SimConnect's event handle,
/// dispatches messages, and runs calls queued from other threads, so the wrapper is never
/// used concurrently. No window handle is needed.
/// </summary>
public sealed class ManagedSimConnectClient : ISimConnectClient
{
    private const uint UserObject = 0; // SIMCONNECT_OBJECT_ID_USER
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly Channel<SimConnectMessage> _messages = Channel.CreateUnbounded<SimConnectMessage>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly ConcurrentQueue<Action<Sdk>> _calls = new();
    private readonly Dictionary<string, uint> _events = new(StringComparer.Ordinal); // dispatch thread only
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public ChannelReader<SimConnectMessage> Messages => _messages.Reader;

    public Task OpenAsync(string clientName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (_thread is not null)
        {
            throw new InvalidOperationException("A SimConnect client opens once; make a new one to reconnect.");
        }

        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(clientName, opened)) { IsBackground = true, Name = "SimConnect dispatch" };
        _thread.Start();
        return opened.Task.WaitAsync(ct);
    }

    public void WatchValue(uint id, string simVar, string unit, SimConnectPeriod period) => _calls.Enqueue(sim =>
    {
        sim.AddToDataDefinition((Definition)id, simVar, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, Sdk.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<SingleValue>((Definition)id);
        sim.RequestDataOnSimObject((Request)id, (Definition)id, UserObject, ToNative(period), SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
    });

    public void TransmitEvent(string eventName, uint data) => _calls.Enqueue(sim =>
    {
        if (!_events.TryGetValue(eventName, out uint id))
        {
            id = (uint)_events.Count + 1;
            _events[eventName] = id;
            sim.MapClientEventToSimEvent((ClientEvent)id, eventName);
            sim.AddClientEventToNotificationGroup(Group.Crowsnest, (ClientEvent)id, false);
        }

        sim.TransmitClientEvent(UserObject, (ClientEvent)id, data, Group.Crowsnest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    });

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        _messages.Writer.TryComplete();
        _stop.Dispose();
    }

    private void Run(string clientName, TaskCompletionSource opened)
    {
        using EventWaitHandle signal = new(false, EventResetMode.AutoReset);
        Sdk sim;
        try
        {
            sim = new Sdk(clientName, IntPtr.Zero, 0, signal, 0);
        }
        catch (COMException e)
        {
            opened.SetException(new SimConnectUnavailableException($"SimConnect_Open failed (0x{e.HResult:X8}). Is the sim running?", e));
            _messages.Writer.TryComplete();
            return;
        }

        using (sim)
        {
            bool quit = false;
            sim.OnRecvOpen += (_, data) => _messages.Writer.TryWrite(new SimConnectMessage.Opened(
                data.szApplicationName,
                $"{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor} build {data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}"));
            sim.OnRecvQuit += (_, _) => quit = true;
            sim.OnRecvException += (_, data) => _messages.Writer.TryWrite(new SimConnectMessage.Failed(
                ((SIMCONNECT_EXCEPTION)data.dwException).ToString(), data.dwSendID, data.dwIndex));
            sim.OnRecvSimobjectData += (_, data) => _messages.Writer.TryWrite(new SimConnectMessage.Value(
                data.dwRequestID, ((SingleValue)data.dwData[0]).Value));

            sim.SetNotificationGroupPriority(Group.Crowsnest, Sdk.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
            opened.SetResult();

            while (!quit && !_stop.IsCancellationRequested)
            {
                try
                {
                    while (_calls.TryDequeue(out Action<Sdk>? call))
                    {
                        call(sim);
                    }

                    if (signal.WaitOne(PollInterval))
                    {
                        sim.ReceiveMessage();
                    }
                }
                catch (COMException)
                {
                    // The pipe to the sim broke: it crashed or was killed without a Quit.
                    quit = true;
                }
            }

            if (quit)
            {
                _messages.Writer.TryWrite(new SimConnectMessage.Quit());
            }
        }

        _messages.Writer.TryComplete();
    }

    private static SIMCONNECT_PERIOD ToNative(SimConnectPeriod period) => period switch
    {
        SimConnectPeriod.Once => SIMCONNECT_PERIOD.ONCE,
        SimConnectPeriod.VisualFrame => SIMCONNECT_PERIOD.VISUAL_FRAME,
        SimConnectPeriod.SimFrame => SIMCONNECT_PERIOD.SIM_FRAME,
        SimConnectPeriod.Second => SIMCONNECT_PERIOD.SECOND,
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, null),
    };

    // The wrapper wants enums for ids; any uint cast to these works.
    private enum Definition : uint;

    private enum Request : uint;

    private enum ClientEvent : uint;

    private enum Group : uint
    {
        Crowsnest = 1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SingleValue
    {
        public double Value;
    }
}
