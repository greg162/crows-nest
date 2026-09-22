using System.Management;
using System.Text.RegularExpressions;

namespace Crowsnest.Device;

/// <summary>One candidate serial port, with enough detail to explain why it was picked.</summary>
public sealed record DeviceCandidate(string PortName, string Description, string HardwareId, bool MatchesKnownBoard);

/// <summary>
/// Finds panels (spec §6). SerialPort.GetPortNames() gives no VID/PID, so the hardware ID
/// comes from a CIM query over Win32_PnPEntity.
///
/// The VID/PID filter only ranks candidates — it never excludes one. A board port on other
/// hardware may sit behind a CP210x or CH34x bridge with an entirely different descriptor,
/// and the protocol identifies the device in its hello reply anyway.
/// </summary>
public static partial class DeviceDiscovery
{
    /// <summary>ESP32-S3 native USB-Serial/JTAG, confirmed on the reference panel (spec §9.6 F4).</summary>
    public const string KnownBoardHardwareId = "VID_303A&PID_1001";

    [GeneratedRegex(@"\((?<port>COM\d+)\)", RegexOptions.ExplicitCapture)]
    private static partial Regex PortNameInDescription { get; }

    /// <summary>Known boards first, then everything else, so probing tries the likely port first.</summary>
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

                    candidates.Add(new DeviceCandidate(
                        PortName: match.Groups["port"].Value,
                        Description: name,
                        HardwareId: hardwareId,
                        MatchesKnownBoard: hardwareId.Contains(KnownBoardHardwareId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        // Any port CIM did not describe still deserves a probe.
        foreach (string port in System.IO.Ports.SerialPort.GetPortNames())
        {
            if (!candidates.Any(c => string.Equals(c.PortName, port, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(new DeviceCandidate(port, port, string.Empty, MatchesKnownBoard: false));
            }
        }

        return [.. candidates.OrderByDescending(c => c.MatchesKnownBoard).ThenBy(c => c.PortName, StringComparer.OrdinalIgnoreCase)];
    }
}
