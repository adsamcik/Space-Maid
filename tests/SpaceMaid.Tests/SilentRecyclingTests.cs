using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class SilentRecyclingTests
{
    [Theory]
    [Trait("Stage", "System")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLongNamedFixturesAreRecycledOrKeptWithoutDialogs(bool longRoot)
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, longRoot ? new string('r', 240) : "build")).FullName;
        string file = Path.Join(folder, longRoot ? "keep.txt" : new string('c', 244) + ".bin");
        const string contents = "Owned long-name fixture";
        File.WriteAllText(file, contents);
        Assert.True(file.Length > 260);
        var target = FolderDeletionTarget.CaptureCurrent(folder, contents.Length);
        var outcome = await RecycleWithoutWindows(target);
        if (outcome.Result?.RecycledPath is { } recycled)
        {
            try
            {
                Assert.Null(outcome.Error); Assert.False(outcome.Result.Cancelled);
                Assert.Equal(contents, File.ReadAllText(Path.Join(recycled, Path.GetFileName(file))));
            }
            finally { DeletionTests.RestoreFixture(recycled, folder); }
        }
        else Assert.NotNull(outcome.Error);
        // Some Shell versions shorten long root names to an 8.3 alias. That
        // namespace ambiguity is safely refused, never retried by another path.
        Assert.Equal(contents, File.ReadAllText(file));
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task WindowsUnrecyclableSparseFixtureIsKeptWithoutDialogsOrPermanentFallback()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "too-large-build")).FullName;
        string file = Path.Join(folder, "owned-sparse.bin");
        byte[] contents = "Owned sparse fixture"u8.ToArray();
        File.WriteAllBytes(file, contents);
        const long logicalLength = 1L << 40;
        using (var sparse = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            // Sparse metadata creates an oversized Recycle Bin candidate
            // without allocating its logical size or changing drive settings.
            Assert.True(DeviceIoControl(sparse.SafeFileHandle, 0x000900C4, 0, 0, 0, 0, out _, 0),
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            sparse.SetLength(logicalLength);
        }
        var target = FolderDeletionTarget.CaptureCurrent(folder, logicalLength);
        var events = new List<string>();
        var outcome = await RecycleWithoutWindows(target, (name, _) => events.Add(name));
        try
        {
            // Native Windows first considers recycling, then requests a transfer
            // without TSF_DELETE_RECYCLE_IF_POSSIBLE. The live guard must reject it.
            Assert.Null(outcome.Result); Assert.IsType<IOException>(outcome.Error);
            Assert.Contains("recycle-delete-blocked", events);
            Assert.DoesNotContain("recycle-verified", events);
            Assert.True(Directory.Exists(folder)); Assert.Equal(logicalLength, new FileInfo(file).Length);
            using var preserved = File.OpenRead(file);
            byte[] actual = new byte[contents.Length]; preserved.ReadExactly(actual);
            Assert.Equal(contents, actual);
        }
        finally
        {
            if (outcome.Result?.RecycledPath is { } unexpected) DeletionTests.RestoreFixture(unexpected, folder);
        }
    }

    private static async Task<(RecycleResult? Result, Exception? Error)> RecycleWithoutWindows(
        FolderDeletionTarget target, Action<string, object>? diagnostic = null)
    {
        uint nativeThread = 0;
        var windows = new HashSet<nint>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var operation = FolderRecycler.RecycleAsync(target, 0, cancellation.Token, (name, data) =>
        {
            if (name == "recycle-native-start") Volatile.Write(ref nativeThread, GetCurrentThreadId());
            diagnostic?.Invoke(name, data);
        });
        var timer = Stopwatch.StartNew();
        while (!operation.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            uint thread = Volatile.Read(ref nativeThread);
            if (thread != 0)
                EnumThreadWindows(thread, (window, _) =>
                {
                    if (!IsWindowVisible(window)) return true;
                    windows.Add(window);
                    PostMessageW(window, 0x10, 0, 0); // Close only this fixture operation's native window.
                    return true;
                }, 0);
            await Task.Delay(10);
        }
        Assert.True(operation.IsCompleted, "Windows recycling did not return without user interaction.");
        RecycleResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await operation);
        Assert.Empty(windows);
        return (result, error);
    }

    private delegate bool WindowCallback(nint window, nint state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wparam, nint lparam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, nint input, uint inSize,
        nint output, uint outSize, out uint returned, nint overlapped);
}
