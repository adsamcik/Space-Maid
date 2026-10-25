using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

internal static class ToolCachePaths
{
    internal static string Root(string path)
    {
        path = OptimizationPolicy.NormalizePath(path.Trim());
        if (path.Split(['\\', '/']).Any(part => part.StartsWith('$') || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || part.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Repository metadata and protected filesystem storage cannot be maintained as a tool cache.");
        if (path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)
            || new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Any(p =>
                    path.Equals(Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase)
                    || p == Environment.GetFolderPath(Environment.SpecialFolder.Windows) && OptimizationPolicy.Covers(p, path)))
            throw new InvalidOperationException("A drive, profile, Windows or program installation root cannot be a maintenance target.");
        ValidateAncestors(path, includeLeaf: true);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The tool's configured cache folder is not present.");
        return path;
    }

    internal static void ValidateFile(string path)
    {
        path = OptimizationPolicy.NormalizePath(path);
        ValidateAncestors(path, includeLeaf: false);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0)
            throw new InvalidOperationException("Linked, offline or unavailable tool files cannot be launched.");
    }

    private static void ValidateAncestors(string path, bool includeLeaf)
    {
        for (string? part = includeLeaf ? path : Path.GetDirectoryName(path); part is not null; part = Path.GetDirectoryName(part))
        {
            var attributes = File.GetAttributes(part);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0)
                throw new InvalidOperationException("Linked, offline or unavailable paths cannot be maintained here.");
        }
    }

    internal static unsafe DirectoryLease Lease(string root)
    {
        root = Root(root);
        var paths = new Stack<string>();
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path)) paths.Push(path);
        var handles = new List<SafeFileHandle>();
        try
        {
            ulong volume = 0; FileIdentity identity = default;
            byte* basic = stackalloc byte[40];
            byte* id = stackalloc byte[24];
            foreach (string path in paths)
            {
                using var metadata = GeneratedReadLease.OpenHandle(path, 0x80, 7);
                GeneratedReadLease.CheckLocal(metadata, directory: true);
                var before = GeneratedReadLease.Inspect(metadata, path);
                var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x81, 3, 0, 3, 0x02300000, 0);
                if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
                handles.Add(handle);
                GeneratedReadLease.CheckLocal(handle, directory: true);
                if (!NativeMethods.GetFileInformationByHandleEx(handle, 0, basic, 40)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var attrs = (FileAttributes)LayoutParser.U32(new(basic, 40), 32);
                if ((attrs & FileAttributes.Directory) == 0
                    || (attrs & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0)
                    throw new InvalidOperationException("The cache path changed or became unavailable.");
                if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, id, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                volume = LayoutParser.U64(new(id, 24), 0);
                identity = new(LayoutParser.U64(new(id, 24), 8), LayoutParser.U64(new(id, 24), 16));
                if (before.Volume != volume || before.Identity != identity)
                    throw new IOException("The cache path changed while its directory lease was acquired.");
            }
            if (identity == default) throw new InvalidOperationException("The cache has no verifiable native identity.");
            return new(handles, volume, identity);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    internal sealed class DirectoryLease(List<SafeFileHandle> handles, ulong volume, FileIdentity identity) : IDisposable
    {
        internal ulong Volume { get; } = volume;
        internal FileIdentity Identity { get; } = identity;
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }
}
