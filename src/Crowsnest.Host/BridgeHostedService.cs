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
/// Finds the panel, connects to it, and runs the coordinator between it and the sim (spec §8).
/// When the panel is unplugged or the link fails, the coordinator stops; this disposes the
/// connection and starts looking again.
///
/// The spec's separate <c>DeviceHostedService</c> is folded in here: the coordinator is built
/// around one connected device, so finding the device and running the coordinator are one
/// loop. Multiple panels (§6.5) will split them again.
/// </summary>
public sealed partial class BridgeHostedService(
    PanelSetup setup,
    ISimParameterGateway sim,
    IInputActionMap actions,
    TimeProvider time,
    ILogger<BridgeHostedService> log) : BackgroundService
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool announcedSearching = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            PanelDeviceConnection? device = await FindPanelAsync(stoppingToken).ConfigureAwait(false);
            if (device is null)
            {
                if (!announcedSearching)
                {
                    LogSearching(log, DeviceDiscovery.KnownBoardHardwareId);
                    announcedSearching = true;
                }

                await DelayAsync(SearchInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            announcedSearching = false;
            await using (device.ConfigureAwait(false))
            {
                LogConnected(log, device.Identity!.DeviceType, device.Identity.FirmwareVersion, device.Identity.HardwareId);
                device.LogReceived = line => LogFirmware(log, line.Level, line.Message);

                PanelCoordinator coordinator = new(setup, sim, device, actions, TuningOptions.Default, time, e => LogEffectFailed(log, e));
                try
                {
                    await coordinator.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogPanelLost(log, e);
                }
            }
        }
    }

    /// <summary>Tries each port showing the known board's USB id and keeps the first that answers the handshake.</summary>
    private async Task<PanelDeviceConnection?> FindPanelAsync(CancellationToken ct)
    {
        IEnumerable<DeviceCandidate> candidates = DeviceDiscovery.Enumerate()
            .Where(c => c.MatchesKnownBoard)
            .OrderBy(c => c.PortName, StringComparer.OrdinalIgnoreCase);

        foreach (DeviceCandidate candidate in candidates)
        {
            PanelDeviceConnection device = new(new SerialPortTransport(candidate.PortName));
            try
            {
                await device.ConnectAsync(ct).ConfigureAwait(false);
                return device;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Busy ("Access is denied": another program holds the port) or not a panel.
                LogCandidateRejected(log, candidate.PortName, e.Message);
                await device.DisposeAsync().ConfigureAwait(false);
            }
        }

        return null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Looking for a panel ({HardwareId}) on USB")]
    private static partial void LogSearching(ILogger logger, string hardwareId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel connected: {DeviceType} {Firmware}, id {HardwareId}")]
    private static partial void LogConnected(ILogger logger, string deviceType, string firmware, string hardwareId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Panel firmware [{Level}] {Message}")]
    private static partial void LogFirmware(ILogger logger, string level, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Port} is not an available panel: {Reason}")]
    private static partial void LogCandidateRejected(ILogger logger, string port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the panel; looking for it again")]
    private static partial void LogPanelLost(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sim write, swap or render failed")]
    private static partial void LogEffectFailed(ILogger logger, Exception error);
}
