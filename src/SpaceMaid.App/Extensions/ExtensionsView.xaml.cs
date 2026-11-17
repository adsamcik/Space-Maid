using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceMaid.App.Extensions;

internal partial class ExtensionsView : UserControl
{
    private readonly ExtensionRuntime _runtime;
    private readonly Func<LoadedExtension, bool> _open;
    private readonly List<LoadedExtension> _entries;
    private readonly Dictionary<string, string> _contextErrors = new(StringComparer.OrdinalIgnoreCase);
    private Func<Task>? _retry;
    internal Func<Task>? Retry { get => _retry; init { _retry = value; RetryButton.IsEnabled = value is not null && !_runtime.IsAdministrator; } }
    private bool _retrying;
    private LoadedExtension? Selected => ExtensionList.SelectedItem as LoadedExtension;

    internal ExtensionsView(ExtensionRuntime runtime, Func<LoadedExtension, bool> open)
    {
        _runtime = runtime; _open = open; _entries = runtime.Entries.ToList();
        InitializeComponent();
        RetryButton.IsEnabled = false;
        AddButton.IsEnabled = !runtime.IsAdministrator;
        StatusText.Text = runtime.IsAdministrator ? "Reopen Space Maid without administrator access to use extensions." : runtime.SettingsError ?? "";
        RefreshList();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ExtensionList is not null) RefreshList(); }
    internal void ReloadEntries()
    {
        var pending = _entries.Where(entry => entry.Status == "Available after restart"
            && !_runtime.Entries.Any(loaded => loaded.Id == entry.Id)).ToArray();
        _entries.Clear(); _entries.AddRange(_runtime.Entries); _entries.AddRange(pending); RefreshList();
    }
    private void RefreshList()
    {
        var selected = Selected;
        string query = SearchBox.Text.Trim();
        var filtered = _entries.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ExtensionList.ItemsSource = filtered;
        ExtensionList.SelectedItem = filtered.FirstOrDefault(entry => entry.Id == selected?.Id
            || entry.Directory.Equals(selected?.Directory, StringComparison.OrdinalIgnoreCase)) ?? filtered.FirstOrDefault();
        EmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDetails();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => ShowDetails();
    private void ShowDetails()
    {
        if (DetailsPanel is null) return;
        DetailsPanel.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (Selected is not { } entry) return;
        NameText.Text = entry.Name; VersionText.Text = entry.Version is null ? entry.Status : "Version " + entry.Version;
        EnabledBox.IsChecked = _runtime.IsEnabled(entry); EnabledBox.IsEnabled = !_runtime.IsAdministrator && !_retrying;
        bool contextUnavailable = _contextErrors.ContainsKey(entry.Id);
        StateText.Text = _runtime.HasPendingChange(entry) ? (EnabledBox.IsChecked == true ? "Will be enabled after restart" : "Will be disabled after restart")
            : contextUnavailable ? "Current folder unavailable · Retry initialization" : entry.Status;
        OpenButton.Visibility = entry.View is null ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.IsEnabled = !_retrying && !contextUnavailable;
        OpenButton.ToolTip = contextUnavailable ? "Retry initialization to restore this workflow's current folder." : null;
        IntegrationText.Text = entry.View is null
            ? (entry.SupportsOpportunities ? "Contributes findings to Opportunities." : "This extension has no workspace view.")
            : (contextUnavailable ? "Workflow paused until its folder context is available." : "Available in the main workspace.")
                + (entry.SupportsOpportunities ? " Also contributes findings to Opportunities." : "");
        ErrorText.Text = entry.Error ?? _contextErrors.GetValueOrDefault(entry.Id) ?? "";
        ErrorText.Visibility = ErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IdText.Text = entry.Id; LocationText.Text = entry.Directory;
    }
    internal void ShowContextError(string id, string error) { _contextErrors[id] = error; ShowDetails(); }
    internal void ClearContextError(string id) { if (_contextErrors.Remove(id)) ShowDetails(); }
    internal void ShowStartupError(string error) => StatusText.Text = error;
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retrying || Retry is null) return;
        _retrying = true; RetryButton.IsEnabled = AddButton.IsEnabled = EnabledBox.IsEnabled = ExtensionList.IsEnabled = false;
        try { await Retry(); ReloadEntries(); }
        catch (Exception error) { StatusText.Text = "Retry could not finish: " + error.GetBaseException().Message; }
        finally { _retrying = false; ExtensionList.IsEnabled = true; RetryButton.IsEnabled = Retry is not null && !_runtime.IsAdministrator; AddButton.IsEnabled = !_runtime.IsAdministrator; ShowDetails(); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    { if (Selected is { } entry && !_open(entry)) StatusText.Text = "This workflow is unavailable. Choose Retry initialization."; }
    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        try { _runtime.SetEnabled(entry, EnabledBox.IsChecked == true); StatusText.Text = "Extension settings saved. Restart Space Maid to apply changes."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Settings were not saved: " + error.Message; }
        ShowDetails();
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose trusted extension folder", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        if (_runtime.TryAddTrustedDirectory(picker.FolderName, out var manifest, out var error))
        {
            var added = manifest!;
            _entries.Add(new(added.Id, added.Name, picker.FolderName, "Available after restart", Version: added.Version));
            SearchBox.Text = ""; RefreshList();
            ExtensionList.SelectedItem = _entries[^1];
            StatusText.Text = "Extension added. Restart Space Maid to load it.";
        }
        else StatusText.Text = "Extension was not added: " + error;
    }
}
