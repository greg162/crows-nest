using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crowsnest.Host;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Serilog;

namespace Crowsnest.Tray;

/// <summary>
/// The tray icon and its menu (spec §8). The icon's colour carries the status, the tooltip says
/// which of sim and devices are up, and the menu lists each device and what it shows. A device
/// with no panels raises a notification pointing at the settings file (§6.2).
///
/// Health changes arrive on any thread; everything here runs on the dispatcher.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private static readonly Dictionary<HealthLight, System.Drawing.Icon> Icons = new()
    {
        [HealthLight.Grey] = Dot(Color.FromRgb(0x8A, 0x8F, 0x98)),
        [HealthLight.Amber] = Dot(Color.FromRgb(0xF2, 0xA9, 0x00)),
        [HealthLight.Green] = Dot(Color.FromRgb(0x2E, 0xB8, 0x72)),
    };

    private readonly HealthSnapshotProvider _health;
    private readonly SettingsStore _settings;
    private readonly StartupRegistration _startup;
    private readonly Dispatcher _dispatcher;
    private readonly TaskbarIcon _icon;
    private readonly ContextMenu _menu = new();
    private readonly Lock _gate = new();
    private HealthLight? _shownLight;
    private bool _refreshQueued;

    public TrayIconController(HealthSnapshotProvider health, SettingsStore settings, StartupRegistration startup, Dispatcher dispatcher)
    {
        _health = health;
        _settings = settings;
        _startup = startup;
        _dispatcher = dispatcher;

        _icon = new TaskbarIcon
        {
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
            NoLeftClickDelay = true,
        };
        _menu.Opened += (_, _) => BuildMenu(); // the Start with Windows tick is read afresh
        _icon.TrayBalloonTipClicked += (_, _) => OpenSettings();

        // Not efficiency mode, which ForceCreate turns on by default: Windows would throttle the
        // process, and with it the knob-to-sim loop.
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _health.Changed += QueueRefresh;
        _health.UnassignedDeviceArrived += OnUnassignedDeviceArrived;
        Refresh();
    }

    public void Dispose()
    {
        _health.Changed -= QueueRefresh;
        _health.UnassignedDeviceArrived -= OnUnassignedDeviceArrived;
        _icon.Dispose();
    }

    /// <summary>Changes come in bursts (a device arriving, the sim connecting), so one refresh covers a burst.</summary>
    private void QueueRefresh()
    {
        lock (_gate)
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;
        }

        _dispatcher.BeginInvoke(() =>
        {
            lock (_gate)
            {
                _refreshQueued = false;
            }

            Refresh();
        });
    }

    private void Refresh()
    {
        if (_icon.IsDisposed)
        {
            return;
        }

        HealthSnapshot health = _health.Current;
        if (_shownLight != health.Light)
        {
            _icon.UpdateIcon(Icons[health.Light]);
            _shownLight = health.Light;
        }

        _icon.ToolTipText = health.ToolTip;
        if (_menu.IsOpen)
        {
            BuildMenu();
        }
    }

    private void BuildMenu()
    {
        HealthSnapshot health = _health.Current;
        _menu.Items.Clear();

        _menu.Items.Add(Status(health.SimLine));
        foreach (string line in health.DeviceLines.Concat(health.PortLines))
        {
            _menu.Items.Add(Status(line));
        }

        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Open settings", OpenSettings));
        _menu.Items.Add(Item("Open logs folder", OpenLogsFolder));
        MenuItem startup = Item("Start with Windows", ToggleStartup);
        startup.IsCheckable = true;
        startup.IsChecked = _startup.IsEnabled;
        _menu.Items.Add(startup);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Exit Crowsnest", () => System.Windows.Application.Current.Shutdown()));
    }

    private void OnUnassignedDeviceArrived(DeviceHealth device) => _dispatcher.BeginInvoke(() =>
    {
        if (!_icon.IsDisposed)
        {
            _icon.ShowNotification(
                $"Device {device.Identity.ShortId} has no panels",
                $"Click here to open settings, then add \"{device.Identity.ShortId}\" under \"devices\" with the panels it should show.",
                NotificationIcon.Info);
        }
    });

    private void ToggleStartup()
    {
        if (_startup.IsEnabled)
        {
            _startup.Disable();
        }
        else
        {
            _startup.Enable();
        }

        Log.Information("Start with Windows is now {State}", _startup.IsEnabled ? "on" : "off");
    }

    private void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settings.FilePath) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Nothing is associated with .json on this PC.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_settings.FilePath}\""))?.Dispose();
        }
    }

    private static void OpenLogsFolder()
    {
        Directory.CreateDirectory(App.LogsFolder);
        Process.Start(new ProcessStartInfo(App.LogsFolder) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>A line of status, not a command. Underscores doubled, or WPF takes them as access keys.</summary>
    private static MenuItem Status(string text) => new() { Header = text.Replace("_", "__", StringComparison.Ordinal), IsEnabled = false };

    private static MenuItem Item(string text, Action action)
    {
        MenuItem item = new() { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// A filled circle with a dark rim, readable on light and dark taskbars alike. Drawn by WPF,
    /// then wrapped as a one-image .ico holding a PNG, since H.NotifyIcon's IconSource does not
    /// take a rendered bitmap.
    /// </summary>
    private static System.Drawing.Icon Dot(Color fill)
    {
        const int Size = 32;
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x26)), 3), new Point(Size / 2.0, Size / 2.0), (Size / 2.0) - 3, (Size / 2.0) - 3);
        }

        RenderTargetBitmap bitmap = new(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream png = new();
        encoder.Save(png);

        using MemoryStream ico = new();
        using (BinaryWriter writer = new(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0); // reserved
            writer.Write((short)1); // icon
            writer.Write((short)1); // one image
            writer.Write((byte)Size);
            writer.Write((byte)Size);
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((short)1); // colour planes
            writer.Write((short)32); // bits per pixel
            writer.Write((int)png.Length);
            writer.Write(6 + 16); // the image follows this header and its one entry
            writer.Write(png.ToArray());
        }

        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }
}
