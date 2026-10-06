using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Application.Settings;
using Crowsnest.Device;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Host;

/// <summary>
/// Feeds the tray (spec §8): the sim's connection, the connected devices and what each one
/// shows, and the ports that could not be used. <see cref="Current"/> is built fresh from those on every read, so a listener that
/// reads it after <see cref="Changed"/> always sees the latest, whatever order changes arrive in.
/// </summary>
public sealed partial class HealthSnapshotProvider : IDisposable
{
    private readonly PanelSetup _setup;
    private readonly SettingsStore _settings;
    private readonly DeviceManager _devices;
    private readonly ILogger<HealthSnapshotProvider> _log;
    private readonly IDisposable _simWatch;
    private volatile SimConnectionState _sim = SimConnectionState.Disconnected;

    public HealthSnapshotProvider(
        PanelSetup setup, SettingsStore settings, DeviceManager devices, ISimParameterGateway sim, ILogger<HealthSnapshotProvider> log)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(log);

        _setup = setup;
        _settings = settings;
        _devices = devices;
        _log = log;

        _settings.Changed += OnSettingsChanged;
        _devices.RosterChanged += OnRosterChanged;
        _devices.UnavailableChanged += OnUnavailableChanged;
        _simWatch = sim.ConnectionState.Subscribe(new SimWatch(this));
    }

    public HealthSnapshot Current
    {
        get
        {
            BridgeSettings settings = _settings.Current;
            return new HealthSnapshot(_sim, [.. _devices.Connected.Select(d => Describe(d, settings))], _devices.Unavailable);
        }
    }

    /// <summary>Something in <see cref="Current"/> may have changed. Raised on whichever thread noticed.</summary>
    public event Action? Changed;

    /// <summary>
    /// A device arrived that shows the unassigned screen, so the user has a settings entry to
    /// add (spec §6.2). Raised on whichever thread noticed.
    /// </summary>
    public event Action<DeviceHealth>? UnassignedDeviceArrived;

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _devices.RosterChanged -= OnRosterChanged;
        _devices.UnavailableChanged -= OnUnavailableChanged;
        _simWatch.Dispose();
    }

    private DeviceHealth Describe(ConnectedDevice device, BridgeSettings settings)
    {
        DeviceSettings? entry = settings.Find(device.Identity.HardwareId);
        IReadOnlyList<string> panels = [.. (entry?.Panels ?? []).Where(p => _setup.Panels.Any(known => known.PanelId == p))];
        return new DeviceHealth(device.Identity, device.Port, entry?.Name ?? device.Identity.ShortId, panels);
    }

    private void OnSettingsChanged(BridgeSettings changed) => Raise(Changed);

    private void OnUnavailableChanged() => Raise(Changed);

    private void OnRosterChanged(RosterChange change)
    {
        Raise(Changed);
        if (change is RosterChange.Arrived arrived && Describe(arrived.Device, _settings.Current) is { Assigned: false } device)
        {
            Raise(UnassignedDeviceArrived, device);
        }
    }

    private void OnSimState(SimConnectionState state)
    {
        _sim = state;
        Raise(Changed);
    }

    /// <summary>One listener at a time, so one that throws cannot keep the change from the rest, nor reach the device or sim threads.</summary>
    private void Raise(Action? handlers)
    {
        foreach (Action handler in handlers?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                LogListenerFailed(_log, e);
            }
        }
    }

    private void Raise<T>(Action<T>? handlers, T value)
    {
        foreach (Action<T> handler in handlers?.GetInvocationList().Cast<Action<T>>() ?? [])
        {
            try
            {
                handler(value);
            }
            catch (Exception e)
            {
                LogListenerFailed(_log, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A health listener failed")]
    private static partial void LogListenerFailed(ILogger logger, Exception error);

    private sealed class SimWatch(HealthSnapshotProvider owner) : IObserver<SimConnectionState>
    {
        public void OnNext(SimConnectionState value) => owner.OnSimState(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
