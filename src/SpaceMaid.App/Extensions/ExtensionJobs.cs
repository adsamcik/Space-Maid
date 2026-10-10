using System.Diagnostics;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed record ExtensionJobRecord(Guid Id, string Owner, string Workflow, ExtensionJobState State,
    ExtensionJobProgress? Progress, DateTime StartedUtc, string? Error = null);

// Admission is bounded independently from the one content/two thumbnail lanes.
internal sealed class ExtensionJobs : IAsyncDisposable
{
    private readonly SemaphoreSlim _admission = new(128, 128), _content = new(1, 1), _thumbnails = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly List<ExtensionJobRecord> _records = [];
    private readonly HashSet<Task> _running = [];
    private readonly Action<string, object> _log;
    private TaskCompletionSource _discoveryChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, int> _discoveryOwners = [];
    private readonly AsyncLocal<Guid?> _currentJob = new();
    private bool _foregroundDiscovery;
    private bool _disposed;
    internal event Action? Changed;
    internal IReadOnlyList<ExtensionJobRecord> Recent { get { lock (_gate) return _records.ToArray(); } }

    internal ExtensionJobs(Action<string, object> log) => _log = log;
    internal IExtensionJobs ForOwner(string owner) => new OwnerJobs(this, owner);
    internal void SetDiscoveryBusy(bool busy)
    {
        lock (_gate)
        {
            _foregroundDiscovery = busy; PulseDiscovery();
        }
    }
    internal async Task WaitForDiscoveryAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                Guid owner = _currentJob.Value ?? Guid.Empty;
                if (!_foregroundDiscovery && !_discoveryOwners.Keys.Any(id => id != owner)) return;
                wait = _discoveryChanged.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }
    internal IDisposable BeginDiscovery()
    {
        Guid owner = _currentJob.Value ?? Guid.Empty;
        lock (_gate) { _discoveryOwners[owner] = _discoveryOwners.GetValueOrDefault(owner) + 1; PulseDiscovery(); }
        return new DiscoveryLease(this, owner);
    }
    private void EndDiscovery(Guid owner)
    {
        lock (_gate)
        {
            if (_discoveryOwners.TryGetValue(owner, out int count))
                if (count == 1) _discoveryOwners.Remove(owner); else _discoveryOwners[owner] = count - 1;
            PulseDiscovery();
        }
    }
    private void PulseDiscovery()
    { var previous = _discoveryChanged; _discoveryChanged = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
    private Task RunAsync(string owner, string workflow, JobResource resource, Func<IExtensionJobContext, Task> work,
        IProgress<ExtensionJobProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(resource)) throw new ArgumentOutOfRangeException(nameof(resource));
        if (string.IsNullOrWhiteSpace(workflow) || workflow.Length > 100) throw new ArgumentException("A short workflow name is required.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_admission.Wait(0)) throw new InvalidOperationException("The extension job queue is full (128 requests). Try again after current work finishes.");
            var job = new ExtensionJobRecord(Guid.NewGuid(), owner, workflow, ExtensionJobState.Queued, null, DateTime.UtcNow);
            _records.Add(job); TrimRecords();
            Task task = Task.Run(() => ExecuteAsync(job, resource, work, progress, token));
            _running.Add(task);
            _ = task.ContinueWith(done => { lock (_gate) _running.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Changed?.Invoke();
            return task;
        }
    }
    private async Task ExecuteAsync(ExtensionJobRecord job, JobResource resource, Func<IExtensionJobContext, Task> work,
        IProgress<ExtensionJobProgress>? progress, CancellationToken callerToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token);
        var lane = resource == JobResource.Thumbnail ? _thumbnails : _content;
        bool entered = false;
        var timer = Stopwatch.StartNew();
        try
        {
            await lane.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            _currentJob.Value = job.Id;
            await WaitForDiscoveryAsync(cancellation.Token).ConfigureAwait(false);
            Update(job.Id, ExtensionJobState.Running, null);
            _log("extension-job-start", new { job.Id, job.Owner, job.Workflow, Resource = resource.ToString(), QueueWaitMs = timer.Elapsed.TotalMilliseconds });
            var context = new Context(this, cancellation.Token, value =>
            {
                Update(job.Id, ExtensionJobState.Running, value); progress?.Report(value);
            });
            await work(context).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            Update(job.Id, context.NeedsAttention ? ExtensionJobState.NeedsAttention : ExtensionJobState.Completed, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Update(job.Id, ExtensionJobState.Cancelled, null); throw; }
        catch (Exception ex)
        { Update(job.Id, ExtensionJobState.Failed, null, ex.Message); throw; }
        finally
        {
            _log("extension-job-finish", new { job.Id, job.Owner, job.Workflow, ElapsedMs = timer.Elapsed.TotalMilliseconds,
                State = Recent.FirstOrDefault(item => item.Id == job.Id)?.State.ToString() });
            if (entered) lane.Release();
            _admission.Release();
        }
    }
    private void Update(Guid id, ExtensionJobState state, ExtensionJobProgress? progress, string? error = null)
    {
        lock (_gate)
        {
            int index = _records.FindIndex(value => value.Id == id);
            if (index >= 0) _records[index] = _records[index] with { State = state, Progress = progress ?? _records[index].Progress, Error = error };
            TrimRecords();
        }
        Changed?.Invoke();
    }
    private void TrimRecords()
    {
        while (_records.Count > 160)
        {
            int removable = _records.FindIndex(value => value.State is ExtensionJobState.Completed or ExtensionJobState.Failed or ExtensionJobState.Cancelled or ExtensionJobState.NeedsAttention);
            if (removable < 0) break;
            _records.RemoveAt(removable);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate) { if (_disposed) return; _disposed = true; _shutdown.Cancel(); PulseDiscovery(); running = _running.ToArray(); }
        try { await Task.WhenAll(running).ConfigureAwait(false); } catch (Exception) { /* Outcomes were recorded individually. */ }
        _shutdown.Dispose(); _admission.Dispose(); _content.Dispose(); _thumbnails.Dispose();
    }
    private sealed class OwnerJobs(ExtensionJobs scheduler, string owner) : IExtensionJobs
    {
        public Task RunAsync(string workflow, JobResource resource, Func<IExtensionJobContext, Task> work,
            IProgress<ExtensionJobProgress>? progress, CancellationToken token) => scheduler.RunAsync(owner, workflow, resource, work, progress, token);
    }
    private sealed class Context(ExtensionJobs scheduler, CancellationToken token, Action<ExtensionJobProgress> report) : IExtensionJobContext
    {
        private int _needsAttention;
        internal bool NeedsAttention => Volatile.Read(ref _needsAttention) != 0;
        public CancellationToken Cancellation => token;
        public Task CheckpointAsync() => scheduler.WaitForDiscoveryAsync(token);
        public void Report(ExtensionJobProgress progress)
        {
            if (progress.NeedsAttention) Interlocked.Exchange(ref _needsAttention, 1);
            report(progress);
        }
    }
    private sealed class DiscoveryLease(ExtensionJobs scheduler, Guid owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) scheduler.EndDiscovery(owner); }
    }
}
