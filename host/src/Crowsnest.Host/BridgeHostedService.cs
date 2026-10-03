using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Device;
using Crowsnest.Device.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Host;

/// <summary>
/// Runs the coordinator between the sim and every panel on USB (spec §6.2, §8). One coordinator
/// lives as long as the bridge, so panels share tuning state and a panel that drops out and comes
/// back finds the sessions as it left them. Every 2 s the ports we do not already hold are
/// probed; each panel found runs until its link fails, is disposed, and is found again by a
/// later scan.
///
/// The spec's separate <c>DeviceHostedService</c> is still folded in here; the scan below is the
/// seed of <c>IPanelDeviceManager</c>.
/// </summary>
public sealed partial class BridgeHostedService(
    PanelSetup setup,
    ISimParameterGateway sim,
    IInputActionMap actions,
    TimeProvider time,
    ILogger<BridgeHostedService> log) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PanelCoordinator coordinator = new(setup, sim, actions, TuningOptions.Default, time, e => LogEffectFailed(log, e));
            using CancellationTokenSource runStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task run = coordinator.RunAsync(runStop.Token);
            Task scan = ScanAsync(coordinator, runStop.Token);

            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Only the sim's stream failing ends the run. Start again rather than stop the bridge.
                LogBridgeFailed(log, e);
            }

            await runStop.CancelAsync().ConfigureAwait(false);
            await scan.ConfigureAwait(false);
            await DelayAsync(ScanInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Finds panels and serves each one until <paramref name="ct"/> is cancelled, then waits for them all to close.</summary>
    private async Task ScanAsync(PanelCoordinator coordinator, CancellationToken ct)
    {
        // Ports we hold, so a scan never reopens our own panel ("Access is denied"). Only this
        // loop touches it. The key is a port because that is what probing needs; the panel's
        // identity is its hardware id, and the coordinator keys on that (spec §6.2).
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

                IReadOnlyList<DeviceCandidate> candidates = [.. DeviceDiscovery.Enumerate().Where(c => c.MatchesKnownBoard)];
                refused.IntersectWith(candidates.Select(c => c.PortName)); // unplugged: complain afresh next time

                foreach (DeviceCandidate candidate in candidates.Where(c => !held.ContainsKey(c.PortName)))
                {
                    if (await ProbeAsync(candidate.PortName, refused, ct).ConfigureAwait(false) is { } device)
                    {
                        refused.Remove(candidate.PortName);
                        held[candidate.PortName] = ServeAsync(coordinator, candidate.PortName, device, ct);
                    }
                }

                if (held.Count == 0 && !announcedSearching)
                {
                    LogSearching(log, DeviceDiscovery.KnownBoardHardwareId);
                }

                announcedSearching = held.Count == 0;
                await DelayAsync(ScanInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await Task.WhenAll(held.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a port and completes the handshake, or returns null.</summary>
    private async Task<PanelDeviceConnection?> ProbeAsync(string port, HashSet<string> refused, CancellationToken ct)
    {
        PanelDeviceConnection device = new(new SerialPortTransport(port));
        try
        {
            await device.ConnectAsync(ct).ConfigureAwait(false);
            return device;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Busy ("Access is denied": another program holds the port) or not a panel. Said once
            // per port, since the scan asks again every 2 s.
            if (refused.Add(port))
            {
                LogCandidateRejected(log, port, e.Message);
            }

            await device.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            // Shutting down mid-probe: still close the port.
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Serves one panel until its link fails or the bridge stops, then closes it. Never throws.</summary>
    private async Task ServeAsync(PanelCoordinator coordinator, string port, PanelDeviceConnection device, CancellationToken ct)
    {
        string id = device.Identity!.HardwareId;
        try
        {
            await using (device.ConfigureAwait(false))
            {
                LogConnected(log, device.Identity.DeviceType, device.Identity.FirmwareVersion, id, port);
                device.LogReceived = line => LogFirmware(log, FirmwareLevel(line.Level), id, line.Level, line.Message);

                await coordinator.RunPanelAsync(id, device, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The run failing and the teardown of a pulled cable failing both land here. The
            // filter is on shutdown, not on the exception's type: Windows can report a pulled
            // cable as a cancelled read. The link's own fault says what really happened.
            LogPanelLost(log, id, device.Fault ?? e);
        }
        catch (Exception)
        {
            // Shutting down; the close failing does not matter.
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Looking for panels ({HardwareId}) on USB")]
    private static partial void LogSearching(ILogger logger, string hardwareId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel connected: {DeviceType} {Firmware}, id {HardwareId}, on {Port}")]
    private static partial void LogConnected(ILogger logger, string deviceType, string firmware, string hardwareId, string port);

    /// <summary>The panel's own warnings are worth seeing; its chatter is not.</summary>
    private static LogLevel FirmwareLevel(string level) => level switch
    {
        "error" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        _ => LogLevel.Debug,
    };

    [LoggerMessage(Message = "Panel {HardwareId} firmware [{Level}] {Message}")]
    private static partial void LogFirmware(ILogger logger, LogLevel logLevel, string hardwareId, string level, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Port} is not an available panel: {Reason}")]
    private static partial void LogCandidateRejected(ILogger logger, string port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost panel {HardwareId}; looking for it again")]
    private static partial void LogPanelLost(ILogger logger, string hardwareId, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sim write, swap or render failed")]
    private static partial void LogEffectFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The bridge stopped; starting it again")]
    private static partial void LogBridgeFailed(ILogger logger, Exception error);
}
