using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Settings;

namespace Crowsnest.Tray.Settings;

/// <summary>
/// One device in the settings window: its entry in settings.json, whether it is plugged in, and
/// the panel it shows, one of them or none (spec §6.2).
/// </summary>
public sealed class DeviceSettingsViewModel : Observable
{
    private readonly int _defaultBrightness;
    private string _name;
    private int? _brightness;
    private string? _port;
    private PanelChoiceViewModel _panel;

    /// <param name="key">The entry's key in settings.json: a full hardware id or its last six.</param>
    /// <param name="saved">The entry as it is, or null for a device with none yet.</param>
    /// <param name="available">Every panel the host ships, in catalog order.</param>
    public DeviceSettingsViewModel(string key, DeviceSettings? saved, IReadOnlyList<PanelPages> available, int defaultBrightness)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(available);

        Key = key.ToLowerInvariant();
        _defaultBrightness = defaultBrightness;
        _name = saved?.Name ?? "";
        _brightness = saved?.Brightness;

        // None, then the catalog. A panel the file names but the host does not ship is offered
        // too, so saving does not quietly drop it.
        List<PanelChoiceViewModel> choices = [PanelChoiceViewModel.None, .. available.Select(p => PanelChoiceViewModel.For(p.PanelId, available))];
        if (saved?.Panel is { } named && choices.All(c => c.Id != named))
        {
            choices.Add(PanelChoiceViewModel.For(named, available));
        }

        Choices = choices;
        _panel = choices.First(c => c.Id == saved?.Panel);
    }

    public string Key { get; }

    /// <summary>The six characters the device's screen shows.</summary>
    public string ShortId => Key[^6..];

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? ""))
            {
                Raise(nameof(Title));
            }
        }
    }

    /// <summary>What the device list shows: the name, or the short id until it has one.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? ShortId : Name.Trim();

    /// <summary>What it can show: none, then each panel.</summary>
    public IReadOnlyList<PanelChoiceViewModel> Choices { get; }

    /// <summary>What it shows. Never null: <see cref="PanelChoiceViewModel.None"/> for nothing.</summary>
    public PanelChoiceViewModel Panel
    {
        get => _panel;
        set
        {
            if (Set(ref _panel, value ?? PanelChoiceViewModel.None))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>For the device list: "COM", or "not assigned".</summary>
    public string Summary => Panel.Id is null ? "not assigned" : Panel.Label;

    /// <summary>0 to 100. Until it is set, the default, and the entry leaves it out.</summary>
    public int Brightness
    {
        get => _brightness ?? _defaultBrightness;
        set => Set(ref _brightness, Math.Clamp(value, 0, 100));
    }

    /// <summary>The port it is on, or null while it is not plugged in.</summary>
    public string? Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value))
            {
                Raise(nameof(IsConnected));
                Raise(nameof(Status));
            }
        }
    }

    public bool IsConnected => Port is not null;

    public string Status => Port is { } port ? $"Connected on {port}" : "Not connected";

    /// <summary>The entry to save, or null when nothing is set, so an untouched device adds no clutter.</summary>
    public DeviceSettings? ToSettings()
    {
        string? name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        return name is null && Panel.Id is null && _brightness is null ? null : new DeviceSettings(name, Panel.Id, _brightness);
    }
}

/// <summary>A panel a device can show, or none.</summary>
public sealed class PanelChoiceViewModel
{
    private PanelChoiceViewModel(string? id, string label, string pages)
    {
        Id = id;
        Label = label;
        Pages = pages;
    }

    /// <summary>No panel: the device shows NOT ASSIGNED.</summary>
    public static PanelChoiceViewModel None { get; } = new(null, "None", "the device shows NOT ASSIGNED");

    /// <summary>The panel's id; null for <see cref="None"/>.</summary>
    public string? Id { get; }

    public string Label { get; }

    /// <summary>Its page titles, "COM1, COM2", or why there are none.</summary>
    public string Pages { get; }

    internal static PanelChoiceViewModel For(string id, IReadOnlyList<PanelPages> available)
    {
        PanelPages? panel = available.FirstOrDefault(p => p.PanelId == id);
        string pages = panel switch
        {
            null => "not part of this version of Crowsnest",
            { Pages.Count: 0 } => "no pages yet",
            _ => string.Join(", ", panel.Pages.Select(p => p.Title)),
        };
        return new PanelChoiceViewModel(id, id.ToUpperInvariant(), pages);
    }
}
