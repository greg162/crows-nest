using System.Diagnostics;
using System.IO;
using System.Text;

namespace Crowsnest.Tray;

/// <summary>
/// The firmware flasher (flasher/Flash-Crowsnest.cmd) and the firmware image beside it, which
/// the installer puts in a flasher folder next to Crowsnest.exe. The tray starts the flasher
/// from its menu and compares each board's firmware with the image, to say when one is behind.
/// </summary>
public sealed class Flasher
{
    private const string ScriptName = "Flash-Crowsnest.cmd";

    // As in Flash-Crowsnest.ps1: the app starts 0x20000 into a full image, and ESP-IDF's app
    // description sits 0x20 into the app, a magic word then the version and project name.
    private const int AppDescOffset = 0x20000 + 0x20;
    private const uint AppDescMagic = 0xABCD5432;
    private const string ProjectName = "crowsnest-display";

    private Flasher(string script, string? firmwareVersion)
    {
        Script = script;
        FirmwareVersion = firmwareVersion;
    }

    /// <summary>The .cmd that runs the flasher.</summary>
    public string Script { get; }

    /// <summary>The version of the firmware image beside the script; null when there is none (a run from the repo).</summary>
    public string? FirmwareVersion { get; }

    /// <summary>
    /// The flasher in <c>flasher</c> next to the app, as installed, or in the repo the app was
    /// built in; null when neither has it.
    /// </summary>
    public static Flasher? Find(string appFolder)
    {
        for (DirectoryInfo? folder = new(appFolder); folder is not null; folder = folder.Parent)
        {
            string script = Path.Combine(folder.FullName, "flasher", ScriptName);
            if (File.Exists(script))
            {
                return new Flasher(script, NewestImageVersion(Path.GetDirectoryName(script)!));
            }
        }

        return null;
    }

    /// <summary>Opens the flasher in a console window. It closes Crowsnest itself, once a board is chosen, and starts it again after.</summary>
    public void Start() =>
        Process.Start(new ProcessStartInfo(Script) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Script)! })?.Dispose();

    /// <summary>Whether a board with this firmware is behind the image. False when either version can't be read.</summary>
    public bool IsNewerThan(string boardVersion) =>
        Parse(FirmwareVersion) is { } image && Parse(boardVersion) is { } board && image > board;

    /// <summary>The highest version among the Crowsnest images in a folder, as the flasher picks.</summary>
    private static string? NewestImageVersion(string folder) =>
        Directory.EnumerateFiles(folder, $"{ProjectName}-*.bin")
            .Select(ReadVersion)
            .OfType<string>()
            .MaxBy(Parse);

    /// <summary>The version in a full firmware image's app description, or null when it isn't a Crowsnest image.</summary>
    public static string? ReadVersion(string imagePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(imagePath);
            byte[] buffer = new byte[0x60];
            stream.Position = AppDescOffset;
            stream.ReadExactly(buffer);
            if (BitConverter.ToUInt32(buffer, 0) != AppDescMagic || Text(buffer, 0x30) != ProjectName)
            {
                return null;
            }

            return Text(buffer, 0x10);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null; // too short, or unreadable
        }
    }

    /// <summary>The numbers at the front of a version, so 0.4.3-dirty compares as 0.4.3.</summary>
    private static Version? Parse(string? version)
    {
        string numbers = new([.. (version ?? "").TakeWhile(c => char.IsAsciiDigit(c) || c == '.')]);
        return Version.TryParse(numbers.TrimEnd('.'), out Version? parsed) ? parsed : null;
    }

    private static string Text(byte[] buffer, int offset) => Encoding.ASCII.GetString(buffer, offset, 32).TrimEnd('\0');
}
