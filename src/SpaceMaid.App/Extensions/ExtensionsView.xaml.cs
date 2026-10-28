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
    private LoadedExtension? Selected => ExtensionList.SelectedItem as LoadedExtension;

    internal ExtensionsView(ExtensionRuntime runtime, Func<LoadedExtension, bool> open)
    {
        _runtime = runtime; _open = open; _entries = runtime.Entries.ToList();
        InitializeComponent();
        AddButton.IsEnabled = !runtime.IsAdministrator;
        StatusText.Text = runtime.IsAdministrator ? "Reopen Space Maid without administrator access to use extensions." : runtime.SettingsError ?? "";
        RefreshList();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ExtensionList is not null) RefreshList(); }
    private void RefreshList()
    {
        var selected = Selected;
        string query = SearchBox.Text.Trim();
        var filtered = _entries.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ExtensionList.ItemsSource = filtered;
        ExtensionList.SelectedItem = filtered.FirstOrDefault(entry => entry.Id == selected?.Id) ?? filtered.FirstOrDefault();
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
        EnabledBox.IsChecked = _runtime.IsEnabled(entry); EnabledBox.IsEnabled = !_runtime.IsAdministrator;
        StateText.Text = _runtime.HasPendingChange(entry) ? (EnabledBox.IsChecked == true ? "Will be enabled after restart" : "Will be disabled after restart") : entry.Status;
        OpenButton.Visibility = entry.View is null ? Visibility.Collapsed : Visibility.Visible;
        IntegrationText.Text = entry.View is null
            ? (entry.SupportsOpportunities ? "Contributes findings to Opportunities." : "This extension has no workspace view.")
            : "Available in the main workspace." + (entry.SupportsOpportunities ? " Also contributes findings to Opportunities." : "");
        ErrorText.Text = entry.Error ?? _contextErrors.GetValueOrDefault(entry.Id) ?? "";
        ErrorText.Visibility = ErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IdText.Text = entry.Id; LocationText.Text = entry.Directory;
    }
    internal void ShowContextError(string id, string error) { _contextErrors[id] = error; ShowDetails(); }
    internal void ShowStartupError(string error) => StatusText.Text = error;
    private void Open_Click(object sender, RoutedEventArgs e)
    { if (Selected is { } entry && !_open(entry)) StatusText.Text = "This workflow is unavailable. Reopen Space Maid to retry."; }
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
