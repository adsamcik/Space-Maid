using System.Diagnostics;
using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

public partial class PhotosView
{
    private CancellationTokenSource? _operation;
    private bool _discovering, _liveRefresh;
    private long _operationGeneration;

    private async Task AddFolderAsync(string folder)
    {
        try
        {
            var id = await Task.Run(() => _catalog.AddCollection(folder), _lifetime);
            await LoadCollectionsAsync(id); await DiscoverCurrentAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await DiscoverCurrentAsync();
    private Task DiscoverCurrentAsync()
    {
        if (Collection is not { } collection || _busy) return Task.CompletedTask;
        return RunAsync($"Discovering {collection.Name}", (progress, token) => _service.DiscoverAsync(collection, progress, token), true);
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (Collection is not { } collection || _busy) return;
        await RunAsync($"Analyzing {collection.Name}", (progress, token) => _service.AnalyzeAsync(collection, progress, token));
    }
    private Task RunAsync(string phase, Func<IProgress<ExtensionJobProgress>, CancellationToken, Task> work, bool discovering = false) => Track(RunCoreAsync(phase, work, discovering));
    private async Task RunCoreAsync(string phase, Func<IProgress<ExtensionJobProgress>, CancellationToken, Task> work, bool discovering)
    {
        if (_busy) return;
        _busy = true; _discovering = discovering; _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _pageGeneration++; _pageQuery?.Cancel(); ResetPreviewRequests();
        if (discovering)
        {
            // Discovery revokes content authorization for the whole collection,
            // including locations not yet visited. Keep row/selection identity,
            // but never present the previous analysis as current evidence.
            foreach (var row in _rows) row.Update(WithoutAnalysis(row.Row, _mode) with { Status = "Earlier observation · Refreshing metadata" });
            _ = Track(SelectionAsync());
        }
        UpdateControls();
        SetNotice(phase); JobProgress.Visibility = Visibility.Visible;
        var refreshClock = Stopwatch.StartNew();
        var acceptProgress = true;
        var operationGeneration = ++_operationGeneration;
        var operationCollection = Collection?.Id;
        var operationName = Collection?.Name ?? "Photos";
        var progress = new Progress<ExtensionJobProgress>(value =>
        {
            if (_shuttingDown || !_busy || !acceptProgress || operationGeneration != _operationGeneration) return;
            StatusText.Text = $"{operationName} · {value.Phase} · {value.Completed:N0}" + (value.BytesRead > 0 ? $" · {PhotoFormatting.Bytes(value.BytesRead)} read" : "");
            StatusText.ToolTip = StatusText.Text + (value.Message is null ? "" : $" · {value.Message}");
            if (_discovering && !_liveRefresh && refreshClock.ElapsedMilliseconds >= 1000)
            { refreshClock.Restart(); _liveRefresh = true; _ = Track(RefreshCommittedPageAsync()); }
        });
        try
        {
            await work(progress, _operation.Token);
            acceptProgress = false;
            var completedJob = operationCollection is long id ? await Task.Run(() => _catalog.LastJob(id), _lifetime) : null;
            var needsAttention = completedJob?.Contains("NeedsAttention", StringComparison.Ordinal) == true;
            SetNotice((discovering ? "Discovery finished" : completedJob?.Contains("Completed", StringComparison.Ordinal) == true ? "Analysis complete" : "Analysis finished") + (needsAttention ? " · Needs attention" : ""), needsAttention);
            StatusText.ToolTip = completedJob ?? StatusText.Text;
        }
        catch (OperationCanceledException) { SetNotice("Interrupted · Saved results kept"); }
        catch (Exception error) { ShowFailure(error); }
        finally
        {
            acceptProgress = false; _discovering = false;
            try { if (!_lifetime.IsCancellationRequested) await LoadCollectionsAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowFailure(error); }
            finally
            {
                _busy = false; _operation.Dispose(); _operation = null; JobProgress.Visibility = Visibility.Hidden; UpdateControls();
            }
        }
    }
    private async Task RefreshCommittedPageAsync()
    {
        try { await LoadCollectionsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
        finally { _liveRefresh = false; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); SetNotice("Cancelling · Finishing the current checkpoint"); }
}
