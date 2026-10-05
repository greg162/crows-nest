using Crowsnest.Core.Application.Settings;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Host;

/// <summary>
/// The user's settings file, <c>%LOCALAPPDATA%\Crowsnest\settings.json</c> (spec §8): per user and
/// per machine, because it describes the boards plugged into this PC.
///
/// Created with a commented starter when missing. Saving writes a temporary file and moves it
/// over the real one, so a crash leaves the old settings or the new, never half of each. Edits
/// made by hand are picked up as soon as the file is saved; a file that does not parse is
/// reported and the last good settings are kept.
/// </summary>
public sealed partial class SettingsStore : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private const string Starter = """
        // Crowsnest settings (spec §6.2). Changes apply as soon as you save this file.
        //
        // "devices" says which panels each device shows. Name a device by the six characters its
        // screen shows (or its full 12-character hardware id). It pages through its panels in the
        // order listed. Brightness goes from 0 to 100. For example:
        //
        //   "devices": {
        //     "c81234": { "name": "Radios", "panels": [ "com" ], "brightness": 80 },
        //     "9f44a1": { "name": "Nav", "panels": [ "nav" ] }
        //   }
        {
          "devices": {
          }
        }

        """;

    private readonly ILogger<SettingsStore> _log;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _settle;
    private readonly Lock _gate = new();
    private BridgeSettings _current = BridgeSettings.Empty;

    /// <param name="path">The file; defaults to <see cref="DefaultPath"/>.</param>
    public SettingsStore(ILogger<SettingsStore> log, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        FilePath = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        if (!File.Exists(FilePath))
        {
            WriteAtomically(Starter);
            LogCreated(_log, FilePath);
        }

        Reload(notify: false);

        _settle = new Timer(_ => Reload(notify: true));
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        // Editors save in bursts, and some replace the file by renaming, so wait for quiet.
        FileSystemEventHandler later = (_, _) => _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        _watcher.Changed += later;
        _watcher.Created += later;
        _watcher.Renamed += (_, _) => _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crowsnest", "settings.json");

    public string FilePath { get; }

    public BridgeSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after the file is read again, on a thread-pool thread.</summary>
    public event Action<BridgeSettings>? Changed;

    /// <summary>Replaces the file. The watcher then reloads it, which raises <see cref="Changed"/>.</summary>
    public void Save(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using MemoryStream json = new();
        settings.Write(json);
        WriteAtomically(System.Text.Encoding.UTF8.GetString(json.ToArray()));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _settle.Dispose();
    }

    private void Reload(bool notify)
    {
        BridgeSettings settings;
        try
        {
            using FileStream file = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            settings = BridgeSettings.Read(file);
        }
        catch (InvalidDataException e)
        {
            LogInvalid(_log, FilePath, e.Message);
            return;
        }
        catch (IOException e)
        {
            // Still being written, or gone for a moment mid-replace: the next event retries.
            LogUnreadable(_log, FilePath, e.Message);
            return;
        }

        lock (_gate)
        {
            _current = settings;
        }

        LogLoaded(_log, FilePath, settings.Devices.Count);
        if (notify)
        {
            try
            {
                Changed?.Invoke(settings);
            }
            catch (Exception e)
            {
                // On the timer's thread, where an exception would end the process.
                LogHandlerFailed(_log, e);
            }
        }
    }

    private void WriteAtomically(string text)
    {
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, FilePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Path}; assign panels to devices there")]
    private static partial void LogCreated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings read from {Path}: {Count} device(s)")]
    private static partial void LogLoaded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Path} has a mistake, so the previous settings stay: {Problem}")]
    private static partial void LogInvalid(ILogger logger, string path, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying the new settings failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path} could not be read yet: {Problem}")]
    private static partial void LogUnreadable(ILogger logger, string path, string problem);
}
