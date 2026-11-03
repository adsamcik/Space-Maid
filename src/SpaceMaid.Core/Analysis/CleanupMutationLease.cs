using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SpaceMaid.Core.Analysis;

// A mutex is thread-affine. The dedicated owning thread waits for the async
// operation and releases on that same thread; callers never release after await.
public static class CleanupMutationLease
{
    private static readonly AsyncLocal<LeaseScope?> Current = new();
    private static readonly string Name = CreateName();
    public static bool IsHeldByCurrentContext => Current.Value is { } scope && Volatile.Read(ref scope.Active) == 1;

    public static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        token.ThrowIfCancellationRequested();
        if (IsHeldByCurrentContext) return operation();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(() =>
        {
            Mutex? mutex = null;
            bool entered = false;
            try
            {
                mutex = new Mutex(false, Name);
                int selected;
                try { selected = WaitHandle.WaitAny([mutex, token.WaitHandle]); }
                catch (AbandonedMutexException) { selected = 0; }
                if (selected != 0) throw new OperationCanceledException(token);
                entered = true; token.ThrowIfCancellationRequested();
                var scope = new LeaseScope(); Current.Value = scope;
                try { result.TrySetResult(operation().GetAwaiter().GetResult()); }
                finally { Volatile.Write(ref scope.Active, 0); Current.Value = null; }
            }
            catch (OperationCanceledException) { result.TrySetCanceled(token); }
            catch (Exception error) { result.TrySetException(error); }
            finally { if (entered) mutex!.ReleaseMutex(); mutex?.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return result.Task;
    }

    public static Task RunAsync(Func<Task> operation, CancellationToken token = default)
        => RunAsync(async () => { await operation().ConfigureAwait(false); return true; }, token);

    private static string CreateName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("A Windows user identity is required for cleanup.");
        return @"Global\SpaceMaid.Mutation." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
    }

    private sealed class LeaseScope { internal int Active = 1; }
}
