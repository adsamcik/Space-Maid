using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SpaceMaid.App.Extensions;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ExtensionRuntime? _extensions;
    private readonly CancellationTokenSource _extensionLifetime = new();
    private int _jobsUpdateQueued;
    internal string? ExtensionDataRoot { get; set; }
    internal bool ExtensionsReady { get; private set; }

    private async Task InitializeExtensionsAsync()
    {
        try
        {
            _extensions = new ExtensionRuntime(() => _snapshot, () => _scanStale, RevealExtensionLocation,
                dataRoot: ExtensionDataRoot, logDirectory: Path.Join(LogDirectory, "Extensions"));
            _extensions.JobsChanged += ExtensionJobsChanged;
            _extensions.SetDiscoveryBusy(_work is not null);
            await _extensions.InitializeAsync(_extensionLifetime.Token);
            if (_closing) return;
            var photos = _extensions.Entries.FirstOrDefault(entry => entry.Id == "space-maid.photos");
            if (photos?.View is { } view)
            { PhotosContent.Content = view; PhotosPlaceholder.Visibility = Visibility.Collapsed; }
            else ExtensionsStatusText.Text = Elevation.IsAdministrator
                ? "Reopen Space Maid without administrator access to use Photos and extensions."
                : photos is null ? "Photos unavailable · Check Extensions"
                : $"Photos · {photos.Status}\n{photos.Error}";
            ExtensionsReady = true;
        }
        catch (OperationCanceledException) when (_extensionLifetime.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_closing) ExtensionsStatusText.Text = $"Extensions could not start: {error.Message}"; }
        finally { ExtensionsReady = true; }
    }

    private void Extensions_Click(object sender, RoutedEventArgs e)
    {
        if (_extensions is not null && !_closing) _extensions.CreateManagerWindow(this).ShowDialog();
    }

    private void ExtensionJobsChanged()
    {
        if (_closing || Interlocked.Exchange(ref _jobsUpdateQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _jobsUpdateQueued, 0);
            if (_closing || _extensions is null) return;
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
        if (Directory.Exists(path)) { OpenFolderInExplorer(path); return; }
        if (!File.Exists(path)) { StatusText.Text = "This photo location is no longer available. Refresh the collection."; return; }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + Path.GetFullPath(path));
            using var process = Process.Start(start);
            _explorerRecheckPath = Path.GetDirectoryName(path);
        }
        catch (System.ComponentModel.Win32Exception error) { StatusText.Text = $"Could not reveal the photo: {error.Message}"; }
    }
}
