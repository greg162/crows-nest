using System.Collections.ObjectModel;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Settings;
using Crowsnest.Host;

namespace Crowsnest.Tray.Settings;

/// <summary>
/// The settings window (spec §8): every device settings.json names or that is plugged in, and
/// the one being edited. Nothing is written until <see cref="ToSettings"/> is saved.
/// </summary>
public sealed class SettingsViewModel : Observable
{
    private readonly IReadOnlyList<PanelPages> _available;
    private readonly int _defaultBrightness;
    private DeviceSettingsViewModel? _selected;

    /// <param name="saved">The settings as they are now.</param>
    /// <param name="available">Every panel the host ships, in catalog order.</param>
    /// <param name="connected">The devices plugged in now.</param>
    /// <param name="defaultBrightness">What a device without a brightness of its own gets.</param>
    /// <param name="problem">Why settings.json was last rejected, if it was: saving will replace it.</param>
    public SettingsViewModel(
        BridgeSettings saved, IReadOnlyList<PanelPages> available, IReadOnlyList<DeviceHealth> connected, int defaultBrightness, string? problem = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(connected);

        _available = available;
        _defaultBrightness = defaultBrightness;
        Problem = problem;

        foreach ((string key, DeviceSettings device) in saved.Devices)
        {
            Devices.Add(new DeviceSettingsViewModel(key, device, available, defaultBrightness));
        }

        ShowConnected(connected);

        // Plugged-in devices first, since they are the ones the user can see and is most likely
        // setting up; the rest in the file's order.
        DeviceSettingsViewModel[] ordered = [.. Devices.OrderBy(r => !r.IsConnected)];
        Devices.Clear();
        foreach (DeviceSettingsViewModel row in ordered)
        {
            Devices.Add(row);
        }

        _selected = Devices.FirstOrDefault();
    }

    public ObservableCollection<DeviceSettingsViewModel> Devices { get; } = [];

    public DeviceSettingsViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(CanForget));
            }
        }
    }

    /// <summary>
    /// A device that is not plugged in can be dropped from the file. A plugged-in one stays in the
    /// list (choose None to unassign it), or it would only reappear.
    /// </summary>
    public bool CanForget => Selected is { IsConnected: false };

    public string? Problem { get; }

    public bool HasNoDevices => Devices.Count == 0;

    /// <summary>
    /// Follows devices being plugged in and pulled out while the window is open. A new device
    /// gets a row; one that is pulled out keeps its row, marked not connected.
    /// </summary>
    public void ShowConnected(IReadOnlyList<DeviceHealth> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);

        HashSet<DeviceSettingsViewModel> plugged = [];
        foreach (DeviceHealth device in connected)
        {
            DeviceSettingsViewModel? row = Find(device.Identity.HardwareId);
            if (row is null)
            {
                row = new DeviceSettingsViewModel(device.Identity.ShortId, null, _available, _defaultBrightness);
                Devices.Add(row);
                Raise(nameof(HasNoDevices));
            }

            row.Port = device.Port;
            plugged.Add(row);
        }

        foreach (DeviceSettingsViewModel row in Devices.Where(r => !plugged.Contains(r)))
        {
            row.Port = null;
        }

        Selected ??= Devices.FirstOrDefault();
        Raise(nameof(CanForget));
    }

    /// <summary>Selects a device by its hardware id, as the notification for an unassigned one does.</summary>
    public void Select(string hardwareId)
    {
        if (Find(hardwareId) is { } row)
        {
            Selected = row;
        }
    }

    public void ForgetSelected()
    {
        if (Selected is not { IsConnected: false } row)
        {
            return;
        }

        int index = Devices.IndexOf(row);
        Devices.Remove(row);
        Selected = Devices.Count == 0 ? null : Devices[Math.Min(index, Devices.Count - 1)];
        Raise(nameof(HasNoDevices));
    }

    /// <summary>The file to save. Devices with nothing set are left out.</summary>
    public BridgeSettings ToSettings()
    {
        Dictionary<string, DeviceSettings> devices = [];
        foreach (DeviceSettingsViewModel row in Devices)
        {
            if (row.ToSettings() is { } entry)
            {
                devices[row.Key] = entry;
            }
        }

        return new BridgeSettings(devices);
    }

    /// <summary>The row settings would use for this device: by full id, else by its last six.</summary>
    private DeviceSettingsViewModel? Find(string hardwareId)
    {
        string id = hardwareId.ToLowerInvariant();
        return Devices.FirstOrDefault(r => r.Key == id) ?? Devices.FirstOrDefault(r => r.Key == id[^6..]);
    }
}
