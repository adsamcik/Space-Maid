using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

// No recursive delete and no deletion by a pathname after validation. Pin
// ancestors against rename/reparse replacement, then delete the exact file
// through its exclusive handle. Open files, hard links and changed files stay.
internal static partial class RegenerableFileDeleter
{
    internal static unsafe OptimizationFile Capture(OptimizationFile file, ProofBudget budget, CancellationToken token, int keepRecentDays)
    {
        using var handle = Open(OptimizationPolicy.NormalizePath(file.Path), 0x80, 7);
        var basic = Basic(handle);
        if (basic.Accessed >= DateTime.UtcNow.AddDays(-Math.Max(keepRecentDays, file.Rule.MinimumAgeDays)).ToFileTimeUtc())
            throw new InvalidOperationException("Recent observed access; kept until a later check.");
        byte* standard = stackalloc byte[24]; Read(handle, 1, standard, 24);
        CheckStreams(handle);
        if ((basic.Attributes & (OptimizationPlanner.UnsafeAttributes | FileAttributes.Directory)) != 0
            || basic.Created != file.CreationFileTime || basic.Written != file.LastWriteFileTime
            || LayoutParser.I64(new(standard, 24), 8) != file.LogicalBytes || LayoutParser.U32(new(standard, 24), 16) != 1)
            throw new InvalidOperationException("File changed, is shared or is protected; kept.");
        byte* buffer = stackalloc byte[24]; Read(handle, 18, buffer, 24);
        var identity = new FileIdentity(LayoutParser.U64(new(buffer, 24), 8), LayoutParser.U64(new(buffer, 24), 16));
        if (identity == default || file.Identity is { } original && original != identity)
            throw new InvalidOperationException("File identity changed; kept.");
        using var readable = GeneratedReadLease.Open(file.Path, preserveAccessTime: true);
        using var proof = GeneratedResourceProof.Verify(file, readable.Stream, readable.Revision, budget, token);
        readable.Verify();
        return file with { Identity = identity, VolumeSerialNumber = LayoutParser.U64(new(buffer, 24), 0),
            Evidence = proof.Evidence, Inputs = proof.Inputs, ContentHash = proof.ContentHash };
    }

    internal static unsafe void Delete(OptimizationFile file, OptimizationPolicy policy, CancellationToken token, ProofBudget budget)
    {
        policy = policy.Validate();
        string path = OptimizationPolicy.NormalizePath(file.Path);
        if (!OptimizationPolicy.Covers(policy.ScopePath, path) || !OptimizationPolicy.Covers(file.ContainerPath, path)
            || path.Equals(file.ContainerPath, StringComparison.OrdinalIgnoreCase) || !file.Rule.Matches(Path.GetFileName(path))
            || !policy.EnabledRules.Contains(file.RuleKey, StringComparer.Ordinal)
            || policy.ExcludedPaths.Any(p => OptimizationPolicy.Covers(p, path)))
            throw new InvalidOperationException("This file is outside the enabled optimization rules.");

        var ancestors = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
        var leases = new List<SafeFileHandle>();
        try
        {
            foreach (string parent in ancestors)
            {
                token.ThrowIfCancellationRequested();
                using var metadata = Open(parent, 0x80, 7);
                GeneratedReadLease.CheckLocal(metadata, directory: true);
                var before = GeneratedReadLease.Inspect(metadata, parent);
                var lease = Open(parent, 0x81, 3); // READ_ATTRIBUTES | LIST_DIRECTORY: effective no-rename lease.
                leases.Add(lease);
                var after = GeneratedReadLease.Inspect(lease, parent);
                if (before.Volume != after.Volume || before.Identity != after.Identity) throw new IOException("Ancestor changed during lease acquisition.");
                var info = Basic(lease);
                if ((info.Attributes & FileAttributes.Directory) == 0
                    || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)(0x40000 | 0x400000))) != 0)
                    throw new InvalidOperationException("Linked, offline or replaced paths are kept.");
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.StartsWith('$')
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Protected filesystem paths are kept.");
            }
            using var handle = Open(path, 0x80010180, 0); // READ | DELETE | READ/WRITE_ATTRIBUTES, exclusive.
            GeneratedReadLease.SuppressAccessUpdates(handle);
            GeneratedReadLease.CheckLocal(handle, directory: false);
            var basic = Basic(handle);
            byte* standard = stackalloc byte[24];
            Read(handle, 1, standard, 24);
            CheckStreams(handle);
            var data = new ReadOnlySpan<byte>(standard, 24);
            if ((basic.Attributes & (OptimizationPlanner.UnsafeAttributes | FileAttributes.Directory)) != 0
                || LayoutParser.U32(data, 16) != 1 || standard[20] != 0)
                throw new InvalidOperationException("Shared, protected or unavailable files are kept.");
            if (LayoutParser.I64(data, 8) != file.LogicalBytes || basic.Created != file.CreationFileTime || basic.Written != file.LastWriteFileTime)
                throw new InvalidOperationException("File changed after preview; kept until the next check.");
            long cutoff = DateTime.UtcNow.AddDays(-Math.Max(policy.KeepRecentDays, file.Rule.MinimumAgeDays)).ToFileTimeUtc();
            // Access time is extra positive evidence when supplied by Windows;
            // it is never used to claim that an old file has not been used.
            if (basic.Created <= 0 || basic.Written <= 0 || Math.Max(basic.Created, basic.Written) >= cutoff
                || basic.Accessed >= cutoff)
                throw new InvalidOperationException("Recent file activity; kept.");
            byte* identity = stackalloc byte[24]; Read(handle, 18, identity, 24);
            var id = new FileIdentity(LayoutParser.U64(new(identity, 24), 8), LayoutParser.U64(new(identity, 24), 16));
            if (id == default || file.Identity is not { } expected || id != expected
                || file.VolumeSerialNumber is not { } expectedVolume || LayoutParser.U64(new(identity, 24), 0) != expectedVolume)
                throw new InvalidOperationException("File identity changed; kept.");
            if (file.Evidence?.Disposition != OptimizationDisposition.Eligible || file.ContentHash is null || file.Inputs.Count == 0)
                throw new InvalidOperationException("Current producer evidence is required before deletion.");
            using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.Read, 65536, isAsync: false);
            using var proof = GeneratedResourceProof.Verify(file, stream, GeneratedReadLease.Inspect(handle, path), budget, token);
            token.ThrowIfCancellationRequested();
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, &delete, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); }
    }

    private static SafeFileHandle Open(string path, uint access, uint sharing)
    {
        var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, sharing, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint | 0x00100000, 0); // OPEN_NO_RECALL
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }

    private static unsafe (long Created, long Accessed, long Written, FileAttributes Attributes) Basic(SafeFileHandle handle)
    {
        byte* buffer = stackalloc byte[40]; Read(handle, 0, buffer, 40);
        var data = new ReadOnlySpan<byte>(buffer, 40);
        return (LayoutParser.I64(data, 0), LayoutParser.I64(data, 8), LayoutParser.I64(data, 16), (FileAttributes)LayoutParser.U32(data, 32));
    }
    private static unsafe void Read(SafeFileHandle handle, int kind, byte* buffer, uint length)
    { if (!NativeMethods.GetFileInformationByHandleEx(handle, kind, buffer, length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }

    private static unsafe void CheckStreams(SafeFileHandle handle)
    {
        // FILE_STREAM_INFO: keep files with any alternate stream, including on
        // standard scans that do not inventory those potentially unique bytes.
        byte* buffer = stackalloc byte[1024]; Read(handle, 7, buffer, 1024);
        var data = new ReadOnlySpan<byte>(buffer, 1024);
        if (LayoutParser.U32(data, 0) != 0 || LayoutParser.U32(data, 4) != 14
            || !new ReadOnlySpan<char>(buffer + 24, 7).SequenceEqual("::$DATA"))
            throw new InvalidOperationException("Files with additional data streams are kept.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte* buffer, uint length);
}
