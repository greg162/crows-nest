using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Crowsnest.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Crowsnest.Tray;

/// <summary>
/// The app (spec §8): the bridge from Crowsnest.Host running in this process, and a tray icon
/// that shows how it is doing. No window of its own; it ends from the icon's menu or with the
/// Windows session.
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private SingleInstanceGuard? _guard;
    private IHost? _host;
    private TrayIconController? _tray;

    /// <summary><c>%LOCALAPPDATA%\Crowsnest\logs</c>, beside settings.json.</summary>
    public static string LogsFolder { get; } = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "logs");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // What the host gets in every other project (InvariantGlobalization, Directory.Build.props).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        _guard = SingleInstanceGuard.TryAcquire();
        if (_guard is null)
        {
            MessageBox.Show(
                "Crowsnest is already running. Look for its icon in the notification area (you may need to click ^ to see it).",
                "Crowsnest", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("SourceContext", "Crowsnest.Tray") // for lines not from a host logger
            .WriteTo.File(
                Path.Combine(LogsFolder, "crowsnest-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        Log.Information("Crowsnest {Version} starting", typeof(App).Assembly.GetName().Version);

        try
        {
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders(); // no console here, and not the Windows event log
            builder.Services.AddSerilog(Log.Logger);
            builder.AddCrowsnest();
            _host = builder.Build();

            // Off the UI thread, so nothing the host starts picks up the dispatcher as its context.
            await Task.Run(() => _host.StartAsync()).ConfigureAwait(true);

            _tray = new TrayIconController(
                _host.Services.GetRequiredService<HealthSnapshotProvider>(),
                _host.Services.GetRequiredService<SettingsStore>(),
                new StartupRegistration(Environment.ProcessPath!),
                Dispatcher);
        }
        catch (Exception error)
        {
            Log.Fatal(error, "Crowsnest could not start");
            MessageBox.Show(
                $"Crowsnest could not start:\n\n{error.Message}\n\nThe log in {LogsFolder} has the details.",
                "Crowsnest", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_host is not null)
        {
            // Closes the devices' ports and the SimConnect session. On the pool, so the wait
            // cannot deadlock against anything that wants the dispatcher.
            try
            {
                if (!Task.Run(async () =>
                    {
                        using CancellationTokenSource timeout = new(StopTimeout);
                        await _host.StopAsync(timeout.Token).ConfigureAwait(false);
                    }).Wait(StopTimeout + TimeSpan.FromSeconds(1)))
                {
                    Log.Warning("The bridge did not stop within {Timeout}", StopTimeout);
                }
            }
            catch (Exception error)
            {
                Log.Error(error, "Stopping the bridge failed");
            }

            _host.Dispose();
        }

        Log.Information("Crowsnest stopped");
        Log.CloseAndFlush();
        _guard?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray menu action failing (a folder that will not open, a registry write refused)
        // should not take the bridge down with it.
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;
    }
}
