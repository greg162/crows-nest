using System.Management;
using System.Text.RegularExpressions;

namespace Crowsnest.Device;

/// <summary>One serial port that looks like one of our boards.</summary>
public sealed record DeviceCandidate(string PortName, string Description, string HardwareId);

/// <summary>
/// Finds the serial ports that look like our boards (spec §6), by USB id. SerialPort.GetPortNames()
/// gives no VID/PID, so the ids come from a CIM query over Win32_PnPEntity.
///
/// Only those ports are probed: opening someone else's serial device, a GPS or a flight
/// controller, to send it a hello is rude and can upset it. A board that sits behind a USB
/// bridge (CP210x, CH34x) adds the bridge's id to <see cref="KnownBoardHardwareIds"/>.
/// </summary>
public static partial class DeviceDiscovery
{
    /// <summary>ESP32-S3 native USB-Serial/JTAG, confirmed on the reference device (spec §9.6 F4).</summary>
    public static IReadOnlyList<string> KnownBoardHardwareIds { get; } = ["VID_303A&PID_1001"];

    [GeneratedRegex(@"\((?<port>COM\d+)\)", RegexOptions.ExplicitCapture)]
    private static partial Regex PortNameInDescription { get; }

    /// <summary>The ports whose USB id is a known board's, by port name.</summary>
    public static IReadOnlyList<DeviceCandidate> Enumerate()
    {
        List<DeviceCandidate> candidates = [];

        if (OperatingSystem.IsWindows())
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementBaseObject device in searcher.Get())
            {
                using (device)
                {
                    string name = device["Name"]?.ToString() ?? string.Empty;
                    string hardwareId = device["PNPDeviceID"]?.ToString() ?? string.Empty;

                    Match match = PortNameInDescription.Match(name);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (KnownBoardHardwareIds.Any(id => hardwareId.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(new DeviceCandidate(match.Groups["port"].Value, name, hardwareId));
                    }
                }
            }
        }

        return [.. candidates.OrderBy(c => c.PortName, StringComparer.OrdinalIgnoreCase)];
    }
}
