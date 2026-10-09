using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Application.Settings;
using Crowsnest.Core.Domain.Tuning;
using Crowsnest.Device;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Host;

/// <summary>
/// Runs the coordinator between the sim and the devices <see cref="DeviceManager"/> finds
/// (spec §6.2, §8). One coordinator lives as long as the bridge, so devices share tuning state
/// and a device that drops out and comes back finds the sessions as it left them.
///
/// What each device shows, and how bright it is, comes from <see cref="SettingsStore"/> (§6.2).
/// When the file changes, connected devices are reassigned and sent their brightness again.
/// </summary>
public sealed partial class BridgeHostedService(
    PanelSetup setup,
    SettingsStore settings,
    DeviceManager devices,
    ISimParameterGateway sim,
    IInputActionMap actions,
    TimeProvider time,
    ILogger<BridgeHostedService> log) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private volatile PanelCoordinator? _coordinator;

    private string AvailablePanels => string.Join(", ", setup.Panels.Select(p => p.PanelId));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        CheckPanelNames(settings.Current);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PanelCoordinator coordinator = new(setup, sim, actions, TuningOptions.Default, time, e => LogEffectFailed(log, e), PagesFor);
            _coordinator = coordinator;
            using CancellationTokenSource runStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task run = coordinator.RunAsync(runStop.Token);
            Task serving = devices.RunAsync((device, ct) => ServeAsync(coordinator, device, ct), runStop.Token);

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
            await serving.ConfigureAwait(false);
            await DelayAsync(RestartDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private Task ServeAsync(PanelCoordinator coordinator, ConnectedDevice device, CancellationToken ct)
    {
        DescribeAssignment(device.Identity.HardwareId, settings.Current);
        return coordinator.RunDeviceAsync(device.Identity.HardwareId, device.Connection, ct);
    }

    /// <summary>What a device shows: its panel's pages, or nothing (the unassigned screen). Called on the coordinator's loop.</summary>
    private IReadOnlyList<PanelPage> PagesFor(string hardwareId) =>
        setup.PagesFor(settings.Current.Find(hardwareId)?.Panel);

    /// <summary>On the settings file's watcher thread.</summary>
    private void OnSettingsChanged(BridgeSettings changed)
    {
        CheckPanelNames(changed);
        _coordinator?.Reassign();

        foreach (ConnectedDevice device in devices.Connected)
        {
            DescribeAssignment(device.Identity.HardwareId, changed);
            _ = RefreshAsync(device);
        }
    }

    private async Task RefreshAsync(ConnectedDevice device)
    {
        try
        {
            await device.Connection.RefreshConfigAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Gone, or going: it gets its config again when it is found.
            LogRefreshFailed(log, device.Identity.HardwareId, e.Message);
        }
    }

    private void CheckPanelNames(BridgeSettings current)
    {
        foreach ((string key, DeviceSettings device) in current.Devices)
        {
            if (device.Panel is { } panel && !setup.HasPanel(panel))
            {
                LogUnknownPanel(log, key, panel, AvailablePanels);
            }
        }
    }

    private void DescribeAssignment(string hardwareId, BridgeSettings current)
    {
        string shortId = DeviceIdentity.ShortIdOf(hardwareId);
        DeviceSettings? device = current.Find(hardwareId);
        if (device?.Panel is { } panel && setup.HasPanel(panel))
        {
            LogAssigned(log, hardwareId, device.Name ?? shortId, panel);
        }
        else
        {
            LogUnassigned(log, hardwareId, settings.FilePath, shortId, AvailablePanels);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {HardwareId} ({Name}) shows {Panel}")]
    private static partial void LogAssigned(ILogger logger, string hardwareId, string name, string panel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {HardwareId} has no panels assigned. In {Path}, add \"{ShortId}\" under \"devices\" with the panel it shows: {Available}")]
    private static partial void LogUnassigned(ILogger logger, string hardwareId, string path, string shortId, string available);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings for device {Device} name panel \"{Panel}\", which does not exist. Panels: {Available}")]
    private static partial void LogUnknownPanel(ILogger logger, string device, string panel, string available);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send device {HardwareId} its new settings: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string hardwareId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sim write, swap or render failed")]
    private static partial void LogEffectFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The bridge stopped; starting it again")]
    private static partial void LogBridgeFailed(ILogger logger, Exception error);
}
