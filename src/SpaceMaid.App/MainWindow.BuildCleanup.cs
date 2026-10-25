using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string CleanupSettingsPath { get; set; } = AppData.File("cleanup-settings.json");
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

    private void UpdateBuildFolderContents(OpportunityRow? row)
    {
        BuildFolderContentsPanel.Visibility = row?.Category == "Build output" ? Visibility.Visible : Visibility.Collapsed;
        BuildFolderContentsList.ItemsSource = null;
        BuildFolderContentsNote.Text = "";
        if (row?.Category != "Build output" || _snapshot is not { } scan) return;

        // Keep only the largest entries, even for folders with thousands of files.
        // This reads the existing inventory; no live traversal or content reads.
        const int limit = 8;
        var largest = new PriorityQueue<ScanEntry, long>();
        int count = 0;
        foreach (int link in scan.EnumerateChildren(row.Item.DirectoryIndex))
        {
            var entry = scan.Entry(link); count++;
            largest.Enqueue(entry, entry.AttributedBytes(scan.HasAllocationData));
            if (largest.Count > limit) largest.Dequeue();
        }
        BuildFolderContentsList.ItemsSource = largest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(entry => entry.AttributedBytes(scan.HasAllocationData)).ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new BuildFolderContentRow(entry.Name + (entry.IsDirectory ? "/" : ""),
                !entry.IsPrimaryName ? "Shared" : scan.IsUnmeasuredDirectory(entry.FileIndex) ? "Not checked"
                    : SizeText.Format(entry.AttributedBytes(scan.HasAllocationData)) + (entry.IsIncomplete ? " +" : ""),
                entry.Name + (entry.IsIncomplete ? " · Partially scanned" : "")
                    + (entry.IsShared ? " · Shared file; counted at its primary scanned name" : ""))).ToArray();
        BuildFolderContentsNote.Text = count == 0 ? "No child entries in this scan."
            : (count > limit ? $"Largest {limit} of {count:N0} entries" : $"{count:N0} {(count == 1 ? "entry" : "entries")}")
                + (scan.HasAllocationData ? " · On disk at scan time" : " · Logical size at scan time");
        if (scan.IsUnmeasuredDirectory(row.Item.DirectoryIndex) || scan.Entry(scan.PrimaryLink(row.Item.DirectoryIndex)).IsIncomplete)
            BuildFolderContentsNote.Text += " · Contents may be incomplete.";
    }

    internal sealed record BuildFolderContentRow(string Name, string Size, string Detail);
}
