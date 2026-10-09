using Crowsnest.Core.Application.Ports;
using Crowsnest.Device;

namespace Crowsnest.Host;

/// <summary>The tray icon's colour (spec §8): grey when nothing is up, amber when one of sim or devices is, green when both are.</summary>
public enum HealthLight
{
    Grey,
    Amber,
    Green,
}

/// <summary>A connected device as the tray shows it.</summary>
/// <param name="Name">From settings, or the short id when settings give none.</param>
/// <param name="Panel">The panel it shows; null when it shows the unassigned screen.</param>
public sealed record DeviceHealth(DeviceIdentity Identity, string Port, string Name, string? Panel)
{
    public bool Assigned => Panel is not null;
}

/// <summary>How the bridge is doing right now: what the tray icon, its tooltip and its menu show.</summary>
/// <param name="Ports">Ports that look like a board's but could not be used, such as one another program holds.</param>
public sealed record HealthSnapshot(SimConnectionState Sim, IReadOnlyList<DeviceHealth> Devices, IReadOnlyList<UnavailablePort> Ports)
{
    public HealthLight Light => (Sim == SimConnectionState.Connected, Devices.Count > 0) switch
    {
        (true, true) => HealthLight.Green,
        (false, false) => HealthLight.Grey,
        _ => HealthLight.Amber,
    };

    /// <summary>One line for the sim. <see cref="SimConnectionState.Connecting"/> covers waiting for a flight to load as well.</summary>
    public string SimLine => Sim switch
    {
        SimConnectionState.Connected => "MSFS: connected",
        SimConnectionState.Connecting => "MSFS: connecting",
        SimConnectionState.Faulted => "MSFS: connection failed, retrying",
        _ => "MSFS: not running",
    };

    /// <summary>One line per device, or one saying there are none.</summary>
    public IReadOnlyList<string> DeviceLines => Devices.Count == 0
        ? ["No devices connected"]
        : [.. Devices.Select(Describe)];

    /// <summary>One line per port that could not be used; none when every port is fine.</summary>
    public IReadOnlyList<string> PortLines => [.. Ports.Select(p => p.InUse ? $"{p.Port}: in use by another program" : $"{p.Port}: not answering")];

    /// <summary>For the icon's tooltip, which Windows cuts off at 127 characters.</summary>
    public string ToolTip
    {
        get
        {
            string devices = Devices.Count switch
            {
                0 => "No devices connected",
                1 => Describe(Devices[0]),
                _ => $"{Devices.Count} devices" + (Devices.Count(d => !d.Assigned) is var n and > 0 ? $", {n} not assigned" : ""),
            };
            string ports = Ports.Count switch
            {
                0 => "",
                1 => "\n" + PortLines[0],
                _ => $"\n{Ports.Count} ports unavailable",
            };
            string text = $"Crowsnest\n{SimLine}\n{devices}{ports}";
            return text.Length <= 127 ? text : text[..127];
        }
    }

    private static string Describe(DeviceHealth device)
    {
        string shortId = device.Identity.ShortId;
        string label = device.Name == shortId ? shortId : $"{device.Name} ({shortId})";
        return device.Assigned ? $"{label}: {device.Panel}" : $"{label}: not assigned";
    }
}
