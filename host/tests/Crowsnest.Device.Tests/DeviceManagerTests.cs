using Crowsnest.Device.Simulation;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Device.Tests;

/// <summary>Finding, serving and losing devices (spec §6.2), on pretend USB.</summary>
public sealed class DeviceManagerTests : IAsyncDisposable
{
    private const string A = "a4cb8fdccc6c";
    private const string B = "a4cb8fdc1234";

    private readonly SimulatedUsb _usb = new();
    private readonly ListLogger<DeviceManager> _log = new();
    private readonly List<RosterChange> _roster = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly DeviceManager _manager;
    private Task? _run;

    public DeviceManagerTests()
    {
        _manager = new DeviceManager(
            new DeviceManagerOptions
            {
                ScanInterval = TimeSpan.FromMilliseconds(30),
                FindPorts = _usb.FindPorts,
                Open = _usb.Open,
                Connection = new DeviceConnectionOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(150), PingInterval = TimeSpan.FromMilliseconds(50) },
            },
            _log);
        _manager.RosterChanged += change =>
        {
            lock (_roster)
            {
                _roster.Add(change);
            }
        };
    }

    private IReadOnlyList<RosterChange> Roster
    {
        get
        {
            lock (_roster)
            {
                return [.. _roster];
            }
        }
    }

    /// <summary>Serves a device the way the coordinator does: until its link fails, then throws.</summary>
    private static async Task UntilTheLinkFails(ConnectedDevice device, CancellationToken ct)
    {
        while (device.Connection.Fault is null)
        {
            await Task.Delay(10, ct);
        }

        throw new IOException("link failed");
    }

    private void Start(Func<ConnectedDevice, CancellationToken, Task>? serve = null) =>
        _run = _manager.RunAsync(serve ?? UntilTheLinkFails, _stop.Token);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await _usb.DisposeAsync();
        _stop.Dispose();
    }

    [Fact]
    public async Task ADeviceThatAnswersIsServedAndOnTheRoster()
    {
        _usb.Plug("COM7", A);
        TaskCompletionSource<ConnectedDevice> served = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Start((device, ct) =>
        {
            served.TrySetResult(device);
            return UntilTheLinkFails(device, ct);
        });

        ConnectedDevice device = await served.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(A, device.Identity.HardwareId);
        Assert.Equal("COM7", device.Port);
        Assert.Equal([device], _manager.Connected);
        Assert.Equal(device, Assert.IsType<RosterChange.Arrived>(Assert.Single(Roster)).Device);
    }

    [Fact]
    public async Task APortWeHoldIsNotProbedAgain()
    {
        _usb.Plug("COM7", A);
        Start();
        await Eventually(() => _manager.Connected.Count == 1, "the device");

        await Task.Delay(300); // ten scans

        Assert.Equal(1, _usb.Opens("COM7"));
    }

    [Fact]
    public async Task APulledCableEndsOnlyThatDeviceWhichIsFoundAgainWhenPluggedBackIn()
    {
        _usb.Plug("COM7", A);
        _usb.Plug("COM8", B);
        Start();
        await Eventually(() => _manager.Connected.Count == 2, "both devices");

        await _usb.UnplugAsync("COM7");

        await Eventually(() => Roster.OfType<RosterChange.Left>().Any(), "COM7 to leave");
        RosterChange.Left left = Roster.OfType<RosterChange.Left>().Single();
        Assert.Equal(A, left.Device.Identity.HardwareId);
        Assert.NotNull(left.Reason);
        Assert.Equal([B], _manager.Connected.Select(d => d.Identity.HardwareId));

        _usb.Plug("COM7", A);

        await Eventually(() => _manager.Connected.Count == 2, "COM7 to be found again");
        Assert.Equal(1, _usb.Opens("COM8"));
    }

    [Fact]
    public async Task ADeviceWhoseServingEndsIsClosedAndFoundAgain()
    {
        _usb.Plug("COM7", A);
        int calls = 0;

        Start((device, ct) => Interlocked.Increment(ref calls) == 1
            ? Task.FromException(new InvalidOperationException("refused"))
            : UntilTheLinkFails(device, ct));

        await Eventually(() => Volatile.Read(ref calls) == 2 && _manager.Connected.Count == 1, "the second serve");
        Assert.Equal(2, _usb.Opens("COM7"));
        Assert.IsType<InvalidOperationException>(Roster.OfType<RosterChange.Left>().Single().Reason);
    }

    [Fact]
    public async Task APortThatNeverAnswersIsReportedOnceUntilItIsReplugged()
    {
        _usb.PlugSilent("COM9");
        Start();

        await Eventually(() => _usb.Opens("COM9") >= 3, "a few probes");
        Assert.Equal(1, _log.Count(m => m.Contains("COM9 is not an available device", StringComparison.Ordinal)));
        Assert.Empty(_manager.Connected);

        await _usb.UnplugAsync("COM9");
        await Task.Delay(100);
        _usb.PlugSilent("COM9");

        await Eventually(() => _log.Count(m => m.Contains("COM9 is not an available device", StringComparison.Ordinal)) == 2, "a fresh report");
    }

    [Fact]
    public async Task StoppingClosesEveryDevice()
    {
        _usb.Plug("COM7", A);
        _usb.Plug("COM8", B);
        Start();
        await Eventually(() => _manager.Connected.Count == 2, "both devices");

        await _stop.CancelAsync();
        await _run!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_manager.Connected);
        Assert.All(Roster.OfType<RosterChange.Left>(), left => Assert.Null(left.Reason));
        Assert.Equal(2, Roster.OfType<RosterChange.Left>().Count());
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotStopADeviceBeingServedOrTheOtherListeners()
    {
        _manager.RosterChanged += _ => throw new InvalidOperationException("listener bug");
        int heard = 0;
        _manager.RosterChanged += _ => Interlocked.Increment(ref heard);
        _usb.Plug("COM7", A);
        Start();

        await Eventually(() => _manager.Connected.Count == 1, "the device");
        await Task.Delay(100);

        Assert.Single(_manager.Connected);
        Assert.Equal(1, _usb.Opens("COM7"));
        Assert.Equal(1, Volatile.Read(ref heard));
    }
}

/// <summary>Keeps every formatted message, for asserting on what was logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public int Count(Func<string, bool> match)
    {
        lock (_messages)
        {
            return _messages.Count(match);
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
