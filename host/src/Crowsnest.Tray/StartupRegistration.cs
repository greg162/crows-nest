using Microsoft.Win32;

namespace Crowsnest.Tray;

/// <summary>
/// "Start with Windows" (spec §2 A5, §8): a value under HKCU's Run key, per user, no elevation.
/// Only an entry naming this executable counts as on, so a development build does not claim
/// the installed app's entry, and turning it on points the entry here.
/// </summary>
public sealed class StartupRegistration(string executablePath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Crowsnest";

    private string Command => $"\"{executablePath}\"";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(run?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Enable()
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, Command);
    }

    public void Disable()
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
