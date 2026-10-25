using System.IO;
using SpaceMaid.Extensions.Agents;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AgentAncestorLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedAncestorActivityDoesNotInvalidateExactTargetLeases(bool cursor)
    {
        using var fixture = new TestDirectory();
        var home = new AgentHome(cursor ? AgentTool.Cursor : AgentTool.Codex, Path.Join(fixture.Path, "agent-home"));
        string path = cursor
            ? Directory.CreateDirectory(Path.Join(home.Path, "chats", new string('a', 32), "ef18e304-d68d-4358-8541-b59d25034cdd")).FullName
            : Directory.CreateDirectory(Path.Join(home.Path, "archived_sessions")).FullName;
        string file = Path.Join(path, cursor ? "store.db" : "rollout-old.jsonl");
        File.WriteAllText(file, "Owned unchanged target");
        DateTime old = DateTime.UtcNow.AddDays(-60);
        File.SetLastWriteTimeUtc(file, old); Directory.SetLastWriteTimeUtc(path, old);
        var snapshot = cursor ? CursorChatCleanup.Capture(home, path, default) : null;
        var item = new AgentHistoryItem(home, cursor ? path : file, "Owned fixture",
            snapshot?.RootIdentity ?? AgentFileTarget.Capture(file), snapshot);
        var original = AgentFileTarget.Capture(file);

        // Timestamp changes reproduce another task creating/removing siblings
        // in a shared ancestor without touching any selected recovery data.
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long changes = 0;
        var activity = Task.Factory.StartNew(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Directory.SetLastWriteTimeUtc(fixture.Path, old.AddTicks(Interlocked.Increment(ref changes)));
                    started.TrySetResult(); Thread.Yield();
                }
            }
            catch (Exception error) { started.TrySetException(error); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int attempt = 0; attempt < 64; attempt++)
            {
                using var lease = cursor ? CursorChatCleanup.Lease(item, default) : AgentFileTarget.Lease(item);
                Assert.Equal(original, AgentFileTarget.Capture(file));
            }
        }
        finally
        {
            stop.Cancel(); await activity.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(Interlocked.Read(ref changes) > 1);

        File.AppendAllText(file, "Changed selected bytes");
        Assert.Throws<IOException>(() =>
        {
            using var lease = cursor ? CursorChatCleanup.Lease(item, default) : AgentFileTarget.Lease(item);
        });
        Assert.True(File.Exists(file));
    }
}
