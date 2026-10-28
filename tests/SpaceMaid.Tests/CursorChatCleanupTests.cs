using System.ComponentModel;
using System.IO;
using SpaceMaid.Extensions.Agents;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CursorChatCleanupTests
{
    private const string ChatId = "ef18e304-d68d-4358-8541-b59d25034cdd";

    [Fact]
    public void CaptureKeepsDatabaseAndSidecarsTogetherAndDetectsAnyMemberChange()
    {
        using var fixture = new TestDirectory();
        var (home, path) = Create(fixture.Path);
        string wal = System.IO.Path.Join(path, "store.db-wal"); File.WriteAllText(wal, "owned WAL");
        Age(path);
        var captured = CursorChatCleanup.Capture(home, path, default);
        Assert.Equal(3, captured.Members.Count);
        Assert.Equal(new FileInfo(System.IO.Path.Join(path, "store.db")).Length + new FileInfo(wal).Length, captured.LogicalBytes);
        var item = Item(home, captured); CursorChatCleanup.Validate(item, 30, default);
        File.AppendAllText(wal, "changed");
        Assert.Throws<InvalidOperationException>(() => CursorChatCleanup.Validate(item, 30, default));
        Assert.True(File.Exists(System.IO.Path.Join(path, "store.db")));
    }

    [Fact]
    public void RecentFilesMissingDatabaseAndUnrecognizedRootsAreKept()
    {
        using var fixture = new TestDirectory(); var (home, path) = Create(fixture.Path);
        File.WriteAllText(System.IO.Path.Join(path, "store.db-shm"), "recent active sidecar");
        var captured = CursorChatCleanup.Capture(home, path, default);
        Assert.Throws<InvalidOperationException>(() => CursorChatCleanup.Validate(Item(home, captured), 30, default));
        File.Delete(System.IO.Path.Join(path, "store.db"));
        Assert.Throws<InvalidOperationException>(() => CursorChatCleanup.Capture(home, path, default));
        Assert.False(CursorChatCleanup.IsChatFolder(home, System.IO.Path.Join(home.Path, "chats", "ordinary", ChatId)));
        Assert.False(CursorChatCleanup.IsChatFolder(home, System.IO.Path.Join(home.Path, "User", "globalStorage")));
    }

    [Fact]
    public void MembershipChangesAndReplacementAtSamePathInvalidateThePreview()
    {
        using var fixture = new TestDirectory(); var (home, path) = Create(fixture.Path);
        var captured = CursorChatCleanup.Capture(home, path, default);
        string added = System.IO.Path.Join(path, "new-work.txt"); File.WriteAllText(added, "new unique work"); Age(path);
        Assert.Throws<InvalidOperationException>(() => CursorChatCleanup.Validate(Item(home, captured), 30, default));
        File.Delete(added); Age(path); captured = CursorChatCleanup.Capture(home, path, default);
        Directory.Move(path, path + "-original"); Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Join(path, "store.db"), "same filename, different identity"); Age(path);
        Assert.Throws<InvalidOperationException>(() => CursorChatCleanup.Validate(Item(home, captured), 30, default));
        Assert.True(File.Exists(System.IO.Path.Join(path + "-original", "store.db")));
    }

    [Fact]
    public void ExistingWritersBlockLeasesAndHeldLeasesProtectFileDataAndAncestors()
    {
        using var fixture = new TestDirectory(); var (home, path) = Create(fixture.Path);
        var captured = CursorChatCleanup.Capture(home, path, default); var item = Item(home, captured);
        string db = System.IO.Path.Join(path, "store.db");
        using (var writer = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            Assert.Throws<Win32Exception>(() => CursorChatCleanup.Lease(item, default));
        using (CursorChatCleanup.Lease(item, default))
        {
            Assert.Throws<IOException>(() => File.AppendAllText(db, "must be blocked"));
            string parent = System.IO.Path.GetDirectoryName(path)!;
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        }
        File.AppendAllText(db, "write after release");
    }

    [Fact]
    public void LargeChatAndLinkedMemberAreKeptWithoutFollowingTheLink()
    {
        using var fixture = new TestDirectory(); var (home, path) = Create(fixture.Path);
        for (int i = 0; i < CursorChatCleanup.MaximumEntries; i++) File.WriteAllText(System.IO.Path.Join(path, $"part-{i}.json"), "owned");
        Assert.Throws<IOException>(() => CursorChatCleanup.Capture(home, path, default));
        using var second = new TestDirectory(); var (secondHome, secondPath) = Create(second.Path);
        string outside = Directory.CreateDirectory(System.IO.Path.Join(second.Path, "outside")).FullName;
        string link = System.IO.Path.Join(secondPath, "linked");
        using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { ArgumentList = { "/c", "mklink", "/J", link, outside }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        junction.WaitForExit(); Assert.Equal(0, junction.ExitCode);
        Assert.Throws<IOException>(() => CursorChatCleanup.Capture(secondHome, secondPath, default));
        Directory.Delete(link);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task WindowsRecyclesTheOwnedWholeChatWithItsDatabaseSidecars()
    {
        using var fixture = new TestDirectory(); var (home, path) = Create(fixture.Path);
        File.WriteAllText(System.IO.Path.Join(path, "store.db-wal"), "owned sidecar"); Age(path);
        string kept = System.IO.Path.Join(home.Path, "cli-config.json"); File.WriteAllText(kept, "keep settings");
        var captured = CursorChatCleanup.Capture(home, path, default); var item = Item(home, captured);
        CursorChatCleanup.Validate(item, 30, default);
        var diagnostics = new List<string>();
        try { Assert.True(await CursorChatCleanup.RecycleAsync(item, 0, default, 30, (name, data) => diagnostics.Add(name + ": " + data))); }
        catch (IOException error) { throw new IOException(error.Message + Environment.NewLine + string.Join(Environment.NewLine, diagnostics), error); }
        Assert.False(Directory.Exists(path)); Assert.Equal("keep settings", File.ReadAllText(kept));
    }

    private static AgentHistoryItem Item(AgentHome home, CursorChatSnapshot snapshot)
        => new(home, snapshot.Path, "Cursor CLI chat", snapshot.RootIdentity, snapshot);

    private static (AgentHome Home, string Path) Create(string root)
    {
        var home = new AgentHome(AgentTool.Cursor, System.IO.Path.Join(root, ".cursor"));
        string path = Directory.CreateDirectory(System.IO.Path.Join(home.Path, "chats", new string('a', 32), ChatId)).FullName;
        File.WriteAllText(System.IO.Path.Join(path, "store.db"), "owned fixture; metadata inspection never reads SQLite contents");
        Age(path); return (home, path);
    }
    private static void Age(string path)
    {
        DateTime old = DateTime.UtcNow.AddDays(-60);
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
        foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, old);
        Directory.SetLastWriteTimeUtc(path, old);
    }
}
