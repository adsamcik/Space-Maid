using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string CleanupSettingsPath { get; set; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "cleanup-settings.json");
    internal BuildCleanupPolicy CurrentBuildPolicy { get; private set; } = BuildCleanupPolicy.Default;
    private bool _cleanupSettingsUnavailable;

    internal void LoadBuildCleanupSettings()
    {
        try
        {
            CurrentBuildPolicy = BuildCleanupSettings.Load(CleanupSettingsPath);
            BuildKeepRecentDaysBox.Text = CurrentBuildPolicy.KeepRecentDays.ToString(CultureInfo.InvariantCulture);
            _cleanupSettingsUnavailable = false;
        }
        catch (Exception error) when (SettingsError(error))
        {
            _cleanupSettingsUnavailable = true;
            BuildKeepRecentDaysBox.Text = "3";
            StatusText.Text = "Cleanup settings unavailable. Set build retention in View options before reviewing.";
            ViewOptionsToggle.IsChecked = true;
        }
    }

    private void BuildRetention_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveBuildRetention();
    }
    private void BuildRetention_LostFocus(object sender, RoutedEventArgs e) => SaveBuildRetention();

    internal bool SaveBuildRetention()
    {
        if (_work is not null) return false;
        if (!int.TryParse(BuildKeepRecentDaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365)
        {
            StatusText.Text = "Build retention: enter 1–365 days.";
            return false;
        }
        if (!_cleanupSettingsUnavailable && days == CurrentBuildPolicy.KeepRecentDays) return true;
        try
        {
            var policy = new BuildCleanupPolicy(days);
            BuildCleanupSettings.Save(CleanupSettingsPath, policy);
            CurrentBuildPolicy = policy;
            _cleanupSettingsUnavailable = false;
            StatusText.Text = $"Builds active within {days} days are kept.";
            return true;
        }
        catch (Exception error) when (SettingsError(error))
        {
            _cleanupSettingsUnavailable = true;
            StatusText.Text = "Could not save build retention. Check settings access, then try again.";
            return false;
        }
    }

    private static bool SettingsError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;
}
