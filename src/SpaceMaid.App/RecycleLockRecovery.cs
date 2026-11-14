using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

internal sealed record RecycleLockOwner(int Pid, long Started, int Session, string Name, bool DesktopApp, bool CanClose)
{
    public string DisplayName => $"{Name} (PID {Pid})";
    public string CloseHint => CanClose ? "" : "Close manually if appropriate";
}

internal sealed record RecycleLockReport(RecycleLockOwner[] Owners, int LockedFiles, bool Incomplete);

internal sealed class RecycleLockRecovery(DiskAccess files)
{
    private const int MaximumEntries = 4096;
    private const int MaximumLockedFiles = 128;
    private const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000;
    private const uint CloseWindow = 0x0010;
    private static readonly int CurrentSession = ReadCurrentSession();

    internal RecycleLockReport Inspect(FolderDeletionTarget target, CancellationToken token)
    {
        target.ValidateCurrent();
        if (files.GetDrive(Path.GetPathRoot(target.Path)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Windows app lookup is available for local folders.");
        var locked = new List<(string Path, byte[] Identity, SafeFileHandle Handle)>();
        int seen = 0;
        bool incomplete = false;
        // Pin ancestors as well as the root so namespace changes cannot
        // redirect the walk or the names submitted to Restart Manager.
        var ancestors = new Stack<string>();
        for (string? path = Path.GetDirectoryName(target.Path); path is not null; path = Path.GetDirectoryName(path))
            ancestors.Push(path);
        var pins = new List<SafeFileHandle>();
        try
        {
            foreach (string ancestor in ancestors)
            {
                token.ThrowIfCancellationRequested();
                var pin = Pin(ancestor);
                if (pin is null) return new([], 0, true);
                pins.Add(pin);
            }
            using var root = Pin(target.Path);
            if (root is null) return new([], 0, true);
            target.ValidateCurrent();
            Walk(target.Path, 0);
            // Our metadata handles would otherwise show Space Maid itself as
            // a user of every registered file. Retain the verified identities
            // and pinned parents, and reopen each leaf to check after lookup.
            foreach (var item in locked) item.Handle.Dispose();
            token.ThrowIfCancellationRequested();
            var owners = locked.Count == 0 ? [] : RestartManager.GetOwners(locked.Select(item => item.Path).ToArray());
            foreach (var item in locked)
            {
                using var current = OpenMetadata(item.Path);
                if (current.IsInvalid || !ReadIdentity(current).AsSpan().SequenceEqual(item.Identity))
                    return new([], locked.Count, true);
            }
            target.ValidateCurrent();
            return new(owners, locked.Count, incomplete);
        }
        finally { foreach (var pin in pins) pin.Dispose(); }

        SafeFileHandle? Pin(string directory)
        {
            var handle = files.OpenHandle(ExtendedPath(directory), 0x81, 3, 0, 3, 0x02300000, 0);
            try
            {
                if (!handle.IsInvalid && (files.GetAttributes(directory) & (Unsafe | FileAttributes.Directory)) == FileAttributes.Directory)
                    return handle;
            }
            catch { handle.Dispose(); throw; }
            handle.Dispose();
            return null;
        }

        void Walk(string directory, int depth)
        {
            try { Visit(directory, depth); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { incomplete = true; }
        }

        void Visit(string directory, int depth)
        {
            foreach (var entry in files.EnumerateEntries(directory, ReadEntry, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                token.ThrowIfCancellationRequested();
                if (++seen > MaximumEntries || locked.Count == MaximumLockedFiles) { incomplete = true; return; }
                string name = Path.GetFileName(entry.Path);
                if ((entry.Attributes & Unsafe) != 0 || name.EndsWith('.') || name.EndsWith(' ')
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { incomplete = true; continue; }
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    var child = depth < 64 ? Pin(entry.Path) : null;
                    if (child is null) incomplete = true;
                    else
                    {
                        pins.Add(child);
                        Walk(entry.Path, depth + 1);
                    }
                    continue;
                }
                // Ask for DELETE access without setting any disposition. Only
                // files that deny sharing are registered with Restart Manager.
                using var probe = files.OpenHandle(ExtendedPath(entry.Path), 0x10000, 7, 0, 3, 0x00300000, 0);
                if (probe.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is 32 or 33)
                    {
                        var file = OpenMetadata(entry.Path);
                        if (file.IsInvalid) { file.Dispose(); incomplete = true; continue; }
                        pins.Add(file);
                        locked.Add((entry.Path, ReadIdentity(file), file));
                    }
                    else if (error is not (2 or 3)) incomplete = true;
                }
            }
        }

        SafeFileHandle OpenMetadata(string path) => files.OpenHandle(ExtendedPath(path), 0x80, 7, 0, 3, 0x00300000, 0);

        unsafe byte[] ReadIdentity(SafeFileHandle handle)
        {
            // FileAttributeTagInfo and FileIdInfo are read from the same handle.
            // Reject a link or placeholder even if one replaced an enumerated name.
            uint* attributes = stackalloc uint[2];
            var identity = new byte[24];
            fixed (byte* buffer = identity)
            {
                if (!files.ReadHandleInformation(handle, 9, attributes, 8) ||
                    ((FileAttributes)attributes[0] & (Unsafe | FileAttributes.Directory)) != 0 ||
                    !files.ReadHandleInformation(handle, 18, buffer, 24))
                    throw new IOException("A file changed during the lock check.");
            }
            return identity;
        }
    }

    private static (string Path, FileAttributes Attributes) ReadEntry(ref FileSystemEntry entry)
        => (entry.ToFullPath(), entry.Attributes);
    private static string ExtendedPath(string path) => @"\\?\" + path;

    internal async Task<bool> CloseAndCheckAsync(FolderDeletionTarget target, IReadOnlyList<RecycleLockOwner> approved,
        CancellationToken token)
    {
        var fresh = Inspect(target, token);
        foreach (var owner in fresh.Owners)
        {
            token.ThrowIfCancellationRequested();
            if (!owner.CanClose || !approved.Any(item => item.CanClose && item.Pid == owner.Pid && item.Started == owner.Started)) continue;
            target.ValidateCurrent();
            using var process = OpenDesktopApp(owner);
            if (process is null) continue;
            try
            {
                process.Refresh();
                nint window = process.MainWindowHandle;
                token.ThrowIfCancellationRequested();
                if (window != 0 && IsWindowEnabled(window) && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == owner.Pid)
                    PostMessage(window, CloseWindow, 0, 0);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }

        // Let apps finish saving or ask the user whether to save. Check a
        // bounded number of times, then leave control with the user.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (Inspect(target, token) is { LockedFiles: 0, Incomplete: false }) return true;
            if (attempt < 3) await Task.Delay(1000, token).ConfigureAwait(false);
        }
        return false;
    }

    private static Process? OpenDesktopApp(RecycleLockOwner owner)
    {
        if (!owner.DesktopApp || owner.Session != CurrentSession || owner.Pid == Environment.ProcessId) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(owner.Pid);
            _ = process.SafeHandle; // Retain this process identity across validation and the close request.
            if (!process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == owner.Started
                && process.MainWindowHandle != 0) return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        process?.Dispose();
        return null;
    }

    private static int ReadCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);

    private static class RestartManager
    {
        internal static RecycleLockOwner[] GetOwners(string[] paths)
        {
            Check(RmStartSession(out uint session, 0, new System.Text.StringBuilder(33)));
            try
            {
                Check(RmRegisterResources(session, (uint)paths.Length, paths, 0, 0, 0, 0));
                ProcessInfo[]? entries = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    uint count = (uint)(entries?.Length ?? 0);
                    int status = RmGetList(session, out uint needed, ref count, entries, out _);
                    if (status == 234 && needed <= 1024)
                    {
                        entries = new ProcessInfo[needed];
                        continue;
                    }
                    Check(status);
                    return entries is null ? [] : entries.Take((int)count).Select(ToOwner)
                        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Pid).ToArray();
                }
                throw new IOException("Apps using these files are changing. Check again.");
            }
            finally { RmEndSession(session); }
        }

        private static RecycleLockOwner ToOwner(ProcessInfo info)
        {
            long started = ((long)info.Process.Started.dwHighDateTime << 32) | (uint)info.Process.Started.dwLowDateTime;
            var owner = new RecycleLockOwner(info.Process.Id, started, info.Session,
                string.IsNullOrWhiteSpace(info.Name) ? "Unknown app" : info.Name,
                info.Type is 0 or 1 or 2 or 5 && string.IsNullOrEmpty(info.Service), false);
            using var app = OpenDesktopApp(owner);
            return owner with { CanClose = app is not null };
        }

        private static void Check(int result) { if (result != 0) throw new Win32Exception(result); }

        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess { public int Id; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
            public int Type;
            public uint Status;
            public int Session;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RmStartSession(out uint session, int flags, System.Text.StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
            uint processCount, nint processes, uint serviceCount, nint services);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? apps, out uint reboot);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);
    }
}
