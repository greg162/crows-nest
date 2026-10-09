using System.IO;
using System.Windows;
using Crowsnest.Host;
using Serilog;

namespace Crowsnest.Tray.Settings;

/// <summary>
/// Edits settings.json (spec §8): which panel each device shows, its name and brightness. Save
/// writes the file, and the bridge applies it as it does a hand edit. Cancel or closing leaves it as it was.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;
    private readonly SettingsStore _store;
    private readonly HealthSnapshotProvider _health;

    public SettingsWindow(SettingsViewModel model, SettingsStore store, HealthSnapshotProvider health)
    {
        _model = model;
        _store = store;
        _health = health;
        DataContext = model;
        InitializeComponent();

        _health.Changed += OnHealthChanged;
        Closed += (_, _) => _health.Changed -= OnHealthChanged;
    }

    public void Select(string hardwareId) => _model.Select(hardwareId);

    /// <summary>On whichever thread noticed; the list is the dispatcher's.</summary>
    private void OnHealthChanged() => Dispatcher.BeginInvoke(() => _model.ShowConnected(_health.Current.Devices));

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.Save(_model.ToSettings());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error(error, "Saving settings failed");
            MessageBox.Show(this, $"The settings could not be saved:\n\n{error.Message}", "Crowsnest", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log.Information("Settings saved from the settings window");
        Close();
    }

    private void OnForget(object sender, RoutedEventArgs e) => _model.ForgetSelected();
}
