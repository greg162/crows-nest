using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Crowsnest.Tray.Diagnostics;

/// <summary>
/// The last few hundred log lines, kept in memory so the diagnostics window can show them
/// without reading the files (spec §8). A Serilog sink beside the file sink: every line the
/// file gets, this gets too, from the moment the app starts.
/// </summary>
public sealed class LogTail : ILogEventSink
{
    public const int DefaultCapacity = 500;

    private const string Template = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private readonly MessageTemplateTextFormatter _formatter = new(Template);
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public LogTail(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>A line was added. Raised on whichever thread logged it; keep handlers short.</summary>
    public event Action<string>? LineAdded;

    /// <summary>The kept lines, oldest first.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        using StringWriter text = new();
        _formatter.Format(logEvent, text);
        string line = text.ToString().TrimEnd('\r', '\n');

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        // Outside the lock, and a listener that throws must not stop the logging.
        try
        {
            LineAdded?.Invoke(line);
        }
        catch (Exception)
        {
        }
    }
}
