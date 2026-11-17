using SpaceMaid.Core.IO;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SpaceMaid.App.Extensions;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ExtensionRuntime? _extensions;
    private readonly CancellationTokenSource _extensionLifetime = new();
    private int _jobsUpdateQueued;
    internal string? ExtensionDataRoot { get; set; } = AppData.File("Extensions");
    internal bool ExtensionsReady { get; private set; }
    private Task? _extensionStartup;
    private bool _extensionStartupFailed, _extensionRetrying;
    private OpportunityCatalog OpportunityExtensions => _extensions?.Opportunities
        ?? throw new InvalidOperationException("Cleanup extensions are unavailable. Open Extensions and retry initialization.");
    private IReadOnlyList<Opportunity> DiscoverOpportunities(SpaceMaid.Core.Scanning.ScanSnapshot scan, CancellationToken token)
        => OpportunityExtensions.Find(scan, token);

    private Task InitializeExtensionsAsync() => _extensionStartup ??= InitializeExtensionsCoreAsync();

    private async Task RetryExtensionsAsync()
    {
        if (_closing || _extensionRetrying || _work is not null || _extensionStartup?.IsCompleted == false)
            throw new InvalidOperationException("Wait for the current operation to finish before retrying extensions.");
        _extensionRetrying = true;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
        _work = work; SetBusy(true);
        // Retry owns the foreground UI, not a metadata acquisition. An
        // initializing extension may await its own brokered discovery job.
        _extensions?.SetDiscoveryBusy(false);
        try
        {
            int priorProviders = _extensions?.Entries.Count(entry => entry.SupportsOpportunities) ?? 0;
            var priorBrowser = OpportunityBrowser;
            if (_extensions is not null) await _extensions.RetryFailedAsync(work.Token);
            _extensionStartup = InitializeExtensionsCoreAsync();
            await _extensionStartup;
            if (!_extensionStartupFailed)
            {
                RetryExtensionContexts();
                if (_snapshot is { } scan && (priorProviders != _extensions?.Entries.Count(entry => entry.SupportsOpportunities)
                    || !ReferenceEquals(priorBrowser, OpportunityBrowser)))
                {
                    var selected = _rows.Where(row => row.Selected).Select(row => row.SelectionIdentity).ToHashSet();
                    string? detail = OpportunityBrowser?.Detail?.Path;
                    var refreshed = await Task.Run(() =>
                    {
                        var analysis = OpportunityExtensions.Analyze(scan, work.Token);
                        return (Analysis: analysis, Total: Operations.EstimateCleanup(scan, analysis.Findings, work.Token));
                    }, work.Token);
                    work.Token.ThrowIfCancellationRequested();
                    if (ReferenceEquals(_snapshot, scan))
                    {
                        PublishOpportunities(scan, refreshed.Analysis.Findings, refreshed.Analysis.Groups, selected, detail, preserveScope: true);
                        if (!_scanStale)
                        {
                            CandidateText.Text = SizeText.Format(refreshed.Total.EstimatedBytes);
                            CandidateCaption.Text = $"Observed · {_rows.Count:N0} {(_rows.Count == 1 ? "folder" : "folders")}";
                            OpportunitiesTab.Content = $"Opportunities · {_rows.Count:N0}";
                        }
                    }
                }
                int unavailable = (_extensions?.Entries.Count(entry => entry.Error is not null) ?? 0) + _contextFailures.Count;
                _extensionManager?.ShowStartupError(unavailable == 0 ? "Retry finished. Extensions are available."
                    : $"Retry finished · {unavailable:N0} still unavailable. Select an extension for details.");
            }
        }
        finally { _extensionRetrying = false; _work = null; if (!_closing) SetBusy(false); }
    }

    private async Task InitializeExtensionsCoreAsync()
    {
        try
        {
            if (_extensions is null)
            {
                _extensions = new ExtensionRuntime(() => _snapshot, () => _scanStale, RevealExtensionLocation,
                dataRoot: ExtensionDataRoot, logDirectory: Path.Join(LogDirectory, "Extensions"), evidenceReview: ReviewAssistantEvidenceAsync,
                operations: Operations, opportunityBrowser: new OpportunityBrowserHost(this), navigation: new WorkspaceNavigation(this),
                cloudStorage: new CloudStorageServices(this));
                _extensions.JobsChanged += ExtensionJobsChanged;
            }
            _extensions.SetDiscoveryBusy(_work is not null && !_extensionRetrying);
            await _extensions.InitializeAsync(_extensionLifetime.Token);
            if (_closing) return;
            var opportunities = _extensions.Entries.FirstOrDefault(entry => entry.Id == "space-maid.opportunities");
            if (opportunities?.View is IOpportunityBrowserView browser)
            {
                bool newlyAttached = !ReferenceEquals(OpportunitiesContent.Content, opportunities.View);
                OpportunitiesContent.Content = opportunities.View; OpportunitiesPlaceholder.Visibility = Visibility.Collapsed;
                if (newlyAttached) browser.SetBuildRetention(CurrentBuildPolicy.KeepRecentDays.ToString(System.Globalization.CultureInfo.InvariantCulture), _cleanupSettingsUnavailable);
                browser.SetBusy(_work is not null);
                if (_recycleBinPresentation is { } bin) browser.UpdateRecycleBin(bin);
            }
            else OpportunitiesStatusText.Text = Elevation.IsAdministrator
                ? "Reopen Space Maid without administrator access to use Opportunities and extensions."
                : opportunities is null ? "Opportunities unavailable · Check Extensions" : $"Opportunities · {opportunities.Status}\n{opportunities.Error}";
            var photos = _extensions.Entries.FirstOrDefault(entry => entry.Id == "space-maid.photos");
            if (photos?.View is { } view)
            { PhotosContent.Content = view; PhotosPlaceholder.Visibility = Visibility.Collapsed; }
            else ExtensionsStatusText.Text = Elevation.IsAdministrator
                ? "Reopen Space Maid without administrator access to use Photos and extensions."
                : photos is null ? "Photos unavailable · Check Extensions"
                : $"Photos · {photos.Status}\n{photos.Error}";
            var apps = _extensions.Entries.FirstOrDefault(entry => entry.Id == "space-maid.apps");
            if (apps?.View is { } appsView)
            {
                AppsContent.Content = appsView; AppsPlaceholder.Visibility = Visibility.Collapsed;
                UpdateAppsDrive();
            }
            else AppsStatusText.Text = Elevation.IsAdministrator
                ? "Reopen Space Maid without administrator access to use Apps and extensions."
                : apps is null ? "Apps unavailable · Check Extensions" : $"Apps · {apps.Status}\n{apps.Error}";
            InitializeExtensionWorkspace();
            InitializeCleanupWorkspace();
            _extensionStartupFailed = false;
            ExtensionsReady = true;
        }
        catch (OperationCanceledException) when (_extensionLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing)
            {
                _extensionStartupFailed = true;
                string message = $"Extensions could not start. Retry initialization in Extensions. {error.Message}";
                OpportunitiesStatusText.Text = AppsStatusText.Text = ExtensionsStatusText.Text = message;
                if (_extensions is not null)
                {
                    _extensionManager ??= CreateExtensionManager();
                    ExtensionManagerContent.Content = _extensionManager;
                    _extensionManager.ShowStartupError(message);
                }
                else
                {
                    var panel = new System.Windows.Controls.StackPanel { Margin = new(24) };
                    panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
                    var retry = new System.Windows.Controls.Button { Content = "Retry initialization", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0) };
                    retry.Click += async (_, _) => { retry.IsEnabled = false; try { await RetryExtensionsAsync(); } catch (Exception failure) { StatusText.Text = failure.Message; } finally { retry.IsEnabled = true; } };
                    panel.Children.Add(retry); ExtensionManagerContent.Content = panel;
                }
            }
        }
        finally { ExtensionsReady = true; }
    }

    private void UpdateAppsDrive()
    {
        PublishExtensionContext();
    }

    private void Extensions_Click(object sender, RoutedEventArgs e)
    {
        if (!_closing) ExtensionsTab.IsChecked = true;
    }

    private void ExtensionJobsChanged()
    {
        if (_closing || Interlocked.Exchange(ref _jobsUpdateQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _jobsUpdateQueued, 0);
            if (_closing || _extensions is null) return;
            UpdateUpdateButton();
            var jobs = _extensions.RecentJobs.Where(job => job.State is ExtensionJobState.Queued or ExtensionJobState.Running).ToArray();
            ExtensionJobText.Visibility = jobs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var active = jobs.FirstOrDefault(job => job.State == ExtensionJobState.Running) ?? jobs.FirstOrDefault();
            if (active is not null)
            {
                string name = _extensions.Entries.FirstOrDefault(entry => entry.Id == active.Owner)?.Name ?? active.Owner;
                ExtensionJobText.Text = $"{name} · {active.Progress?.Phase ?? "Waiting"}"
                    + (active.Progress is { } progress ? $" · {progress.Completed:N0} processed" : "")
                    + (jobs.Length > 1 ? $" · {jobs.Length - 1:N0} other jobs" : "");
            }
        }));
    }

    private void RevealExtensionLocation(string path)
    {
        if (DiskAccess.Default.DirectoryExists(path)) { OpenFolderInExplorer(path); return; }
        if (!DiskAccess.Default.FileExists(path)) { StatusText.Text = "This location is no longer available. Refresh its view."; return; }
        try
        {
            _explorerRecheckPath = Operations.OpenInExplorer(path);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { StatusText.Text = $"Could not reveal the location: {error.Message}"; }
    }
}
