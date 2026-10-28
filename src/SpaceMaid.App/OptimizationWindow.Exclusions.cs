using System.IO;
using System.Windows;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

public partial class OptimizationWindow
{
    private void KeepFile_Click(object sender, RoutedEventArgs e) => KeepSelected(folder: false);
    private void KeepFolder_Click(object sender, RoutedEventArgs e) => KeepSelected(folder: true);
    private void KeepSelected(bool folder)
    {
        if (_work is not null || FileList.SelectedItem is not DisplayFile selected) return;
        try { AddExclusions([folder ? selected.Container : selected.Path]); }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Could not add exclusion. " + error.Message; }
    }

    private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Exclude folders and their contents", InitialDirectory = _scopePath, Multiselect = true };
            if (picker.ShowDialog(this) == true) AddExclusions(picker.FolderNames);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Could not add exclusions. " + error.Message; }
    }

    private void AddExcludedFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Exclude files", InitialDirectory = _scopePath, Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(this) == true) AddExclusions(picker.FileNames);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Could not add exclusions. " + error.Message; }
    }

    internal void AddExclusions(IEnumerable<string> paths)
    {
        if (_work is not null) return;
        var additions = paths.Select(OptimizationPolicy.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_exclusions.Any(row => row.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (_exclusions.Count + additions.Length > 256) throw new ArgumentException("Keep up to 256 excluded locations. Remove an exclusion before adding more.");
        if (additions.Length == 0) { ExclusionsTab.IsSelected = true; StatusText.Text = "Already excluded."; return; }
        foreach (string path in additions) _exclusions.Add(new(path));
        ExclusionsChanged();
        ExclusionsList.SelectedItem = _exclusions[^1];
        ExclusionsList.ScrollIntoView(_exclusions[^1]);
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || sender is not System.Windows.Controls.Button { DataContext: ExclusionRow row } button) return;
        int index = _exclusions.IndexOf(row);
        bool restoreFocus = button.IsKeyboardFocusWithin;
        if (!_exclusions.Remove(row)) return;
        ExclusionsChanged();
        if (restoreFocus)
        {
            if (_exclusions.Count == 0) AddExcludedFolderButton.Focus();
            else { ExclusionsList.SelectedIndex = Math.Min(index, _exclusions.Count - 1); ExclusionsList.Focus(); }
        }
    }

    private void UpdateExclusionsView()
    {
        ExclusionsTab.Header = _exclusions.Count == 0 ? "Excluded locations" : $"Excluded locations ({_exclusions.Count})";
        NoExclusionsText.Visibility = _exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExclusionsList.Visibility = _exclusions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ExclusionsChanged()
    {
        UpdateExclusionsView(); ExclusionsTab.IsSelected = true;
        InvalidatePreview();
        PreviewText.Text = SelectedAction?.Rule is not null ? "Exclusions changed. Review files again." : "";
        StatusText.Text = "Save settings to keep these exclusions.";
    }

    internal sealed record ExclusionRow(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
        public string ParentPath => System.IO.Path.GetDirectoryName(Path) ?? "";
    }
}
