using System.Globalization;
using System.Text;
using Crowsnest.Device;
using Crowsnest.Host;
using Crowsnest.Tray.Settings;

namespace Crowsnest.Tray.Diagnostics;

/// <summary>One connected device's row in the diagnostics window, as text ready to show.</summary>
public sealed record DeviceDiagnostics(
    string Title,
    string Port,
    string Panel,
    string Firmware,
    string Connected,
    string RoundTrip,
    long FramesIn,
    long FramesOut,
    long FramesIgnored,
    int Restarts);

/// <summary>
/// What the diagnostics window shows (spec §8): the sim, each device's link, the ports that
/// could not be used, and a report of all that plus the recent log to paste into a bug report.
/// Holds no WPF types; the window calls <see cref="Refresh"/> once a second.
/// </summary>
public sealed class DiagnosticsViewModel(string appVersion) : Observable
{
    private string _simLine = "";
    private IReadOnlyList<DeviceDiagnostics> _devices = [];
    private IReadOnlyList<string> _ports = [];
    private DateTimeOffset _at;

    public string SimLine
    {
        get => _simLine;
        private set => Set(ref _simLine, value);
    }

    public IReadOnlyList<DeviceDiagnostics> Devices
    {
        get => _devices;
        private set => Set(ref _devices, value);
    }

    public bool HasDevices => Devices.Count > 0;

    public bool HasNoDevices => !HasDevices;

    /// <summary>Ports that look like a board's but could not be used; empty when all are fine.</summary>
    public IReadOnlyList<string> Ports
    {
        get => _ports;
        private set => Set(ref _ports, value);
    }

    public bool HasPorts => Ports.Count > 0;

    public void Refresh(HealthSnapshot health, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(health);

        _at = now;
        SimLine = health.SimLine;

        IReadOnlyList<DeviceDiagnostics> devices = [.. health.Devices.Select(d => Describe(d, now))];
        if (!devices.SequenceEqual(Devices))
        {
            Devices = devices;
            Raise(nameof(HasDevices));
            Raise(nameof(HasNoDevices));
        }

        IReadOnlyList<string> ports = health.PortLines;
        if (!ports.SequenceEqual(Ports))
        {
            Ports = ports;
            Raise(nameof(HasPorts));
        }
    }

    /// <summary>Everything on screen as plain text, then the recent log, for a bug report.</summary>
    public string Report(IReadOnlyList<string> logLines)
    {
        ArgumentNullException.ThrowIfNull(logLines);

        StringBuilder report = new();
        report.AppendLine(CultureInfo.InvariantCulture, $"Crowsnest {appVersion}, diagnostics at {_at:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
        report.AppendLine();
        report.AppendLine(SimLine);

        if (Devices.Count == 0)
        {
            report.AppendLine("No devices connected");
        }

        foreach (DeviceDiagnostics d in Devices)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{d.Title} on {d.Port}: panel {d.Panel}, firmware {d.Firmware}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    connected {d.Connected}, round trip {d.RoundTrip}, frames in {d.FramesIn} / out {d.FramesOut}, ignored {d.FramesIgnored}, restarts {d.Restarts}");
        }

        foreach (string port in Ports)
        {
            report.AppendLine(port);
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Recent log ({logLines.Count} lines):");
        foreach (string line in logLines)
        {
            report.AppendLine(line);
        }

        return report.ToString();
    }

    /// <summary>How long a link has been up: "45 s", "12 min 03 s", "2 h 05 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours} h {duration.Minutes:00} min")
            : duration.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes} min {duration.Seconds:00} s")
                : string.Create(CultureInfo.InvariantCulture, $"{duration.Seconds} s");
    }

    /// <summary>"1.8 ms", or a dash before the first pong.</summary>
    public static string FormatRoundTrip(TimeSpan? roundTrip) =>
        roundTrip is { } rtt ? string.Create(CultureInfo.InvariantCulture, $"{rtt.TotalMilliseconds:0.0} ms") : "–";

    private static DeviceDiagnostics Describe(DeviceHealth device, DateTimeOffset now)
    {
        string shortId = device.Identity.ShortId;
        string title = device.Name == shortId ? shortId : $"{device.Name} ({shortId})";
        LinkStats? link = device.Link;

        return new DeviceDiagnostics(
            title,
            device.Port,
            device.Panel?.ToUpperInvariant() ?? "not assigned",
            device.Identity.FirmwareVersion,
            link is null ? "–" : FormatDuration(now - link.ConnectedAt),
            FormatRoundTrip(link?.RoundTrip),
            link?.FramesIn ?? 0,
            link?.FramesOut ?? 0,
            link?.FramesIgnored ?? 0,
            link?.Restarts ?? 0);
    }
}
