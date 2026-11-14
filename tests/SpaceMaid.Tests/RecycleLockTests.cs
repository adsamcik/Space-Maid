using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecycleLockTests
{
    [Fact]
    [Trait("Stage", "System")]
    public async Task ARealFileLockIsListedAndAProcessWithoutAWindowIsKept()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "locked")).FullName;
        string file = Path.Join(folder, "payload.txt");
        File.WriteAllText(file, "Owned lock fixture");
        var operations = new FileOperations();
        var target = FolderDeletionTarget.CaptureCurrent(folder, 18);
        var recovery = new RecycleLockRecovery(operations.Files);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<FolderRecycler.SharingException>(() => operations.RecycleAsync(target, 0, default));
            var report = recovery.Inspect(target, default);
            Assert.Equal(1, report.LockedFiles);
            Assert.False(Assert.Single(report.Owners, owner => owner.Pid == Environment.ProcessId).CanClose);
            Assert.Equal("Owned lock fixture", File.ReadAllText(file));
        }
        var clear = recovery.Inspect(target, default);
        Assert.Equal(0, clear.LockedFiles);
        Assert.Empty(clear.Owners);
        Assert.False(clear.Incomplete);
    }
}
