using System.Windows;
using System.Windows.Threading;
using Crowsnest.Host;

namespace Crowsnest.Tray.Diagnostics;

/// <summary>
/// The diagnostics window (spec §8): the model refreshed once a second, since the link
/// counters change with every frame, and the log tail redrawn as lines arrive. The log
/// follows new lines while it is scrolled to the bottom, and stays put while you read
/// further up.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _model;
    private readonly Func<HealthSnapshot> _health;
    private readonly LogTail _log;
    private readonly DispatcherTimer _timer;
    private readonly Lock _gate = new();
    private bool _logQueued;

    /// <param name="health">The current health, read once a second: <see cref="HealthSnapshotProvider.Current"/>.</param>
    public DiagnosticsWindow(DiagnosticsViewModel model, Func<HealthSnapshot> health, LogTail log)
    {
        InitializeComponent();

        _model = model;
        _health = health;
        _log = log;
        DataContext = model;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshModel(), Dispatcher);
        RefreshModel();
        ShowLog(scrollToEnd: true);
        Loaded += (_, _) => LogBox.ScrollToEnd(); // before layout there is no end to scroll to

        _log.LineAdded += QueueLog;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _log.LineAdded -= QueueLog;
        };
    }

    private void RefreshModel() => _model.Refresh(_health(), DateTimeOffset.Now);

    /// <summary>Lines come in bursts (a device arriving logs several), so one redraw covers a burst.</summary>
    private void QueueLog(string line)
    {
        lock (_gate)
        {
            if (_logQueued)
            {
                return;
            }

            _logQueued = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            lock (_gate)
            {
                _logQueued = false;
            }

            bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 2;
            ShowLog(atEnd);
        });
    }

    private void ShowLog(bool scrollToEnd)
    {
        IReadOnlyList<string> lines = _log.Lines;
        double offset = LogBox.VerticalOffset;

        LogBox.Text = string.Join(Environment.NewLine, lines);
        LogNote.Text = $"the last {lines.Count} lines; older ones are in the logs folder";

        if (scrollToEnd)
        {
            LogBox.ScrollToEnd();
        }
        else
        {
            LogBox.ScrollToVerticalOffset(offset);
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        RefreshModel();
        Clipboard.SetText(_model.Report(_log.Lines));
        ((System.Windows.Controls.Button)sender).Content = "Copied";
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => App.OpenLogsFolder();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
