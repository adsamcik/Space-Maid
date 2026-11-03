using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpaceMaid.App.Extensions;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private readonly Dictionary<string, RadioButton> _extensionLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<FrameworkElement> _contextFailures = [];
    private readonly Dictionary<FrameworkElement, ExtensionViewContext> _publishedContexts = [];
    private ExtensionsView? _extensionManager;

    private void InitializeExtensionWorkspace()
    {
        if (_extensions is null) return;
        foreach (var entry in _extensions.Entries.Where(entry => entry.View is not null))
        {
            RadioButton? link = entry.Id switch
            {
                "space-maid.photos" => PhotosTab,
                "space-maid.apps" => AppsTab,
                "space-maid.opportunities" => OpportunitiesTab,
                _ => null
            };
            if (link is null)
            {
                link = new RadioButton { Content = entry.Name, GroupName = "View", Style = (Style)FindResource("WorkspaceNavigation"), ToolTip = entry.Name };
                var page = new ContentControl { Content = entry.View, Visibility = Visibility.Collapsed,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                ExtensionPages.Children.Add(page);
                link.Checked += (_, _) => View_Changed(link, new RoutedEventArgs());
                link.Tag = page;
                ExtensionNavigation.Children.Add(link);
            }
            SidebarNavigation.SetIcon(link, entry.NavigationIcon ?? SidebarNavigation.GetIcon(link)
                ?? (ImageSource)FindResource("NavigationExtensions"));
            _extensionLinks[entry.Id] = link;
        }
        _extensionManager = new ExtensionsView(_extensions, entry => OpenExtension(entry.Id));
        ExtensionManagerContent.Content = _extensionManager;
        PublishExtensionContext();
        UpdateExtensionWorkspace();
    }

    internal bool OpenExtension(string id)
    {
        if (_closing || string.IsNullOrWhiteSpace(id) || !_extensionLinks.TryGetValue(id, out var link)) return false;
        link.IsChecked = true;
        return true;
    }

    private void UpdateExtensionWorkspace()
    {
        if (ExtensionPages is null || ExtensionsTab is null || ScanHeader is null) return;
        ExtensionPages.IsEnabled = _work is null;
        foreach (var link in _extensionLinks.Values)
            if (link.Tag is ContentControl page) page.Visibility = link.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool extra = _extensionLinks.Values.Any(link => link.Tag is ContentControl && link.IsChecked == true);
        ExtensionPages.Visibility = extra ? Visibility.Visible : Visibility.Collapsed;
        ExtensionManagerContent.Visibility = ExtensionsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // Scanning remains accessible while a foreground operation needs Cancel.
        ScanHeader.Visibility = extra || ExtensionsTab.IsChecked == true || PhotosTab.IsChecked == true
            || OpportunitiesTab.IsChecked == true && !_cleanupBrowserVisible && _cleanupWorkspace?.Prepared is not null
            ? (_work is null ? Visibility.Collapsed : Visibility.Visible) : Visibility.Visible;
        PublishExtensionContext();
    }

    private void PublishExtensionContext()
    {
        if (_extensions is null || _closing) return;
        var context = new ExtensionViewContext(_currentPath, Path.GetPathRoot(_currentPath), _snapshot is not null, _scanStale, _work is not null);
        foreach (var entry in _extensions.Entries)
        {
            if (entry.View is not { } element || _contextFailures.Contains(element)) continue;
            if (_publishedContexts.GetValueOrDefault(entry.View) == context) continue;
            try
            {
                if (element is IExtensionContextView view) view.SetContext(context);
                else if (element is IDriveExtensionView drive && context.DriveRoot is { Length: > 0 } root
                    && _publishedContexts.GetValueOrDefault(element)?.DriveRoot != root) drive.SetDrive(root);
                _publishedContexts[element] = context;
            }
            catch (Exception error)
            {
                _contextFailures.Add(entry.View);
                StatusText.Text = $"{entry.Name} could not receive the current folder. Reopen Space Maid to retry.";
                _extensionManager?.ShowContextError(entry.Id, error.GetBaseException().Message);
            }
        }
    }

    private sealed class WorkspaceNavigation(MainWindow window) : IExtensionNavigation
    {
        public Task<bool> OpenExtensionAsync(string extensionId)
            => OnDispatcherAsync(() => Task.FromResult(window.OpenExtension(extensionId)));
        public Task<bool> ExploreFolderAsync(string path)
            => OnDispatcherAsync(() => window.NavigatePathAsync(path));
        private async Task<bool> OnDispatcherAsync(Func<Task<bool>> action)
        {
            if (window.Dispatcher.HasShutdownStarted) return false;
            return await window.Dispatcher.InvokeAsync(() => window._closing ? Task.FromResult(false) : action()).Task.Unwrap();
        }
    }
}
