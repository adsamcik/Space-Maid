using System.Globalization;
using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

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
            OpportunityBrowser?.SetBuildRetention(CurrentBuildPolicy.KeepRecentDays.ToString(CultureInfo.InvariantCulture), false);
            _cleanupSettingsUnavailable = false;
        }
        catch (Exception error) when (SettingsError(error))
        {
            _cleanupSettingsUnavailable = true;
            OpportunityBrowser?.SetBuildRetention("3", true);
            StatusText.Text = "Cleanup settings unavailable. Set build retention in Cleanup rules before reviewing.";
        }
    }

    internal bool SaveBuildRetention() => SaveBuildRetention(OpportunityBrowser?.BuildRetention
        ?? CurrentBuildPolicy.KeepRecentDays.ToString(CultureInfo.InvariantCulture));

    private bool SaveBuildRetention(string value)
    {
        if (_work is not null) return false;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365)
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
            OpportunityBrowser?.SetBuildRetention(days.ToString(CultureInfo.InvariantCulture), false);
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

    private OpportunityFolderContents ReadBuildFolderContents(Opportunity opportunity)
    {
        if (opportunity.Category != "Build output" || _snapshot is not { } scan
            || !_rows.Any(row => ReferenceEquals(row.Item, opportunity))) return new([], "");
        // Keep only the largest entries, even for folders with thousands of files.
        // This reads the existing inventory; no live traversal or content reads.
        const int limit = 8;
        var largest = new PriorityQueue<ScanEntry, long>();
        int count = 0;
        foreach (int link in scan.EnumerateChildren(opportunity.DirectoryIndex))
        {
            var entry = scan.Entry(link); count++;
            largest.Enqueue(entry, entry.AttributedBytes(scan.HasAllocationData));
            if (largest.Count > limit) largest.Dequeue();
        }
        var items = largest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(entry => entry.AttributedBytes(scan.HasAllocationData)).ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new OpportunityFolderContent(entry.Name + (entry.IsDirectory ? "/" : ""),
                !entry.IsPrimaryName ? "Shared" : scan.IsUnmeasuredDirectory(entry.FileIndex) ? "Not checked"
                    : SizeText.Format(entry.AttributedBytes(scan.HasAllocationData)) + (entry.IsIncomplete ? " +" : ""),
                entry.Name + (entry.IsIncomplete ? " · Partially scanned" : "")
                    + (entry.IsShared ? " · Shared file; counted at its primary scanned name" : ""))).ToArray();
        string note = count == 0 ? "No child entries in this scan."
            : (count > limit ? $"Largest {limit} of {count:N0} entries" : $"{count:N0} {(count == 1 ? "entry" : "entries")}")
                + (scan.HasAllocationData ? " · On disk at scan time" : " · Logical size at scan time");
        if (scan.IsUnmeasuredDirectory(opportunity.DirectoryIndex) || scan.Entry(scan.PrimaryLink(opportunity.DirectoryIndex)).IsIncomplete)
            note += " · Contents may be incomplete.";
        return new(items, note);
    }
}
