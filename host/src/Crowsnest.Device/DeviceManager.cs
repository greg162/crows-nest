using System.Collections.Concurrent;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Device.Transport;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Device;

/// <summary>A device that completed the handshake and is being served, on <paramref name="Port"/> for now.</summary>
public sealed record ConnectedDevice(string Port, DeviceIdentity Identity, DeviceConnection Connection);

/// <summary>A device arrived or left (spec §6.2): what the tray's status and notifications follow.</summary>
public abstract record RosterChange(ConnectedDevice Device)
{
    public sealed record Arrived(ConnectedDevice Device) : RosterChange(Device);

    /// <param name="Reason">Why its link ended; null when the manager was stopped.</param>
    public sealed record Left(ConnectedDevice Device, Exception? Reason) : RosterChange(Device);
}

public sealed record DeviceManagerOptions
{
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The ports worth probing; by default those with a known board's USB id.</summary>
    public Func<IReadOnlyList<string>> FindPorts { get; init; } = () => [.. DeviceDiscovery.Enumerate().Select(c => c.PortName)];

    /// <summary>A transport for a port; tests hand back one end of a loopback.</summary>
    public Func<string, IDeviceTransport> Open { get; init; } = port => new SerialPortTransport(port);

    /// <summary>For every connection, including <see cref="DeviceConnectionOptions.BrightnessFor"/>.</summary>
    public DeviceConnectionOptions Connection { get; init; } = new();

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Finds devices on USB and keeps them (spec §6.2). Every <see cref="DeviceManagerOptions.ScanInterval"/>
/// the ports it does not already hold are probed with a hello; each device that answers is
/// handed to the caller's <c>serve</c> until that returns or throws, then closed, and found
/// again by a later scan if it is still there. One device failing never touches the others.
///
/// Devices are keyed by port only for probing: a port we hold is never reopened, which would
/// fail with "Access is denied". Who a device is comes from its hello (§6.2), and the roster
/// reports that.
/// </summary>
public sealed partial class DeviceManager(DeviceManagerOptions options, ILogger<DeviceManager> log)
{
    private readonly ConcurrentDictionary<string, ConnectedDevice> _connected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The devices being served now, by port name.</summary>
    public IReadOnlyList<ConnectedDevice> Connected => [.. _connected.Values.OrderBy(d => d.Port, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Raised as devices arrive and leave, on whichever thread noticed.</summary>
    public event Action<RosterChange>? RosterChanged;

    /// <summary>
    /// Scans and serves until <paramref name="ct"/> is cancelled, then waits for every device to
    /// close. Never throws for a device's sake.
    /// </summary>
    /// <param name="serve">Runs one device; returning or throwing ends it. Should stop when its token is cancelled.</param>
    public async Task RunAsync(Func<ConnectedDevice, CancellationToken, Task> serve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serve);

        // Only this loop touches these.
        Dictionary<string, Task> held = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase);
        bool announcedSearching = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (string port in held.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
                {
                    held.Remove(port);
                }

                IReadOnlyList<string> ports = options.FindPorts();
                refused.IntersectWith(ports); // unplugged: complain afresh next time

                foreach (string port in ports.Where(p => !held.ContainsKey(p)))
                {
                    if (await ProbeAsync(port, refused, ct).ConfigureAwait(false) is { } device)
                    {
                        refused.Remove(port);
                        held[port] = ServeAsync(new ConnectedDevice(port, device.Identity!, device), serve, ct);
                    }
                }

                if (held.Count == 0 && !announcedSearching)
                {
                    LogSearching(log, string.Join(", ", DeviceDiscovery.KnownBoardHardwareIds));
                }

                announcedSearching = held.Count == 0;
                await DelayAsync(options.ScanInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await Task.WhenAll(held.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a port and completes the handshake, or returns null.</summary>
    private async Task<DeviceConnection?> ProbeAsync(string port, HashSet<string> refused, CancellationToken ct)
    {
        DeviceConnection? device = null;
        try
        {
            // Opening can fail too: the port vanished between the scan and the probe.
            device = new DeviceConnection(options.Open(port), options.Connection, options.Time);
            await device.ConnectAsync(ct).ConfigureAwait(false);
            return device;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Busy ("Access is denied": another program holds the port) or not a Crowsnest device.
            // Said once per port, since the scan asks again every couple of seconds.
            if (refused.Add(port))
            {
                LogCandidateRejected(log, port, e.Message);
            }

            if (device is not null)
            {
                await device.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }
        catch
        {
            // Shutting down mid-probe: still close the port.
            if (device is not null)
            {
                await device.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Serves one device until it ends or the manager stops, then closes it. Never throws.</summary>
    private async Task ServeAsync(ConnectedDevice connected, Func<ConnectedDevice, CancellationToken, Task> serve, CancellationToken ct)
    {
        DeviceConnection device = connected.Connection;
        DeviceIdentity identity = connected.Identity;
        Exception? reason = null;
        try
        {
            await using (device.ConfigureAwait(false))
            {
                LogConnected(log, identity.DeviceType, identity.FirmwareVersion, identity.HardwareId, connected.Port);
                device.LogReceived = line => LogFirmware(log, FirmwareLevel(line.Level), identity.HardwareId, line.Level, line.Message);

                _connected[connected.Port] = connected;
                Raise(new RosterChange.Arrived(connected));
                await serve(connected, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The device failing and the teardown of a pulled cable failing both land here. The
            // filter is on shutdown, not on the exception's type: Windows can report a pulled
            // cable as a cancelled read. The link's own fault says what really happened.
            reason = device.Fault ?? e;
            LogDeviceLost(log, identity.HardwareId, reason);
        }
        catch (Exception)
        {
            // Shutting down; the close failing does not matter.
        }
        finally
        {
            _connected.TryRemove(new KeyValuePair<string, ConnectedDevice>(connected.Port, connected));
            Raise(new RosterChange.Left(connected, reason));
        }
    }

    /// <summary>
    /// One listener at a time: one that throws must not keep the change from the others, nor stop
    /// a device being served or closed.
    /// </summary>
    private void Raise(RosterChange change)
    {
        foreach (Action<RosterChange> listener in RosterChanged?.GetInvocationList().Cast<Action<RosterChange>>() ?? [])
        {
            try
            {
                listener(change);
            }
            catch (Exception e)
            {
                LogListenerFailed(log, e);
            }
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, options.Time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>The device's own warnings are worth seeing; its chatter is not.</summary>
    private static LogLevel FirmwareLevel(string level) => level switch
    {
        "error" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        _ => LogLevel.Debug,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Looking for devices ({HardwareIds}) on USB")]
    private static partial void LogSearching(ILogger logger, string hardwareIds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device connected: {DeviceType} {Firmware}, id {HardwareId}, on {Port}")]
    private static partial void LogConnected(ILogger logger, string deviceType, string firmware, string hardwareId, string port);

    [LoggerMessage(Message = "Device {HardwareId} firmware [{Level}] {Message}")]
    private static partial void LogFirmware(ILogger logger, LogLevel logLevel, string hardwareId, string level, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Port} is not an available device: {Reason}")]
    private static partial void LogCandidateRejected(ILogger logger, string port, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A roster listener failed")]
    private static partial void LogListenerFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost device {HardwareId}; looking for it again")]
    private static partial void LogDeviceLost(ILogger logger, string hardwareId, Exception error);
}
