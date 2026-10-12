using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static SpaceMaid.LocalModel.WindowsSandboxNative;

namespace SpaceMaid.LocalModel;

/// <summary>Holds namespace/content locks and removes only this run's unique SID grants.</summary>
internal sealed class WindowsSandboxFiles : IDisposable
{
    private readonly List<SafeFileHandle> _pins = [];
    private readonly List<SafeFileHandle> _grants = [];
    private readonly Dictionary<string, SafeFileHandle> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecurityIdentifier _sid;
    private bool _disposed;
    private const uint UnsafeAttributes = 0x400 | 0x1000 | 0x40000 | 0x400000;

    internal WindowsSandboxFiles(SecurityIdentifier sid) => _sid = sid;

    internal static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new ArgumentException("The model worker and model must use regular local paths.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        if (root.Length != 3 || full.AsSpan(2).Contains(':') ||
            new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new ArgumentException("The model worker and model must use a local drive.", nameof(path));
        return full;
    }

    internal void GrantRuntime(string directory)
    {
        PinAncestors(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        var count = 0;
        while (pending.TryPop(out var folder))
        {
            var handle = Pin(folder, directory: true, grant: true);
            Grant(handle, 0x1200a9); // Read, execute, synchronize; no inheritance or write rights.
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++count > 4096)
                    throw new IOException("The model worker must be a dedicated runtime package.");
                var attributes = File.GetAttributes(path);
                if (((uint)attributes & UnsafeAttributes) != 0)
                    throw new IOException("The model runtime contains a redirected or unavailable entry.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    // A publish package contains code/configuration, never user documents or model files.
                    // Do not make unrelated files readable simply because they are beside the executable.
                    if (Path.GetExtension(path).ToLowerInvariant() is not (".dll" or ".exe" or ".json" or ".dat"))
                        continue;
                    var file = Pin(path, directory: false, grant: true);
                    Grant(file, 0x1200a9);
                }
            }
        }
    }

    internal void GrantModel(string path)
    {
        PinAncestors(path);
        var file = Pin(path, directory: false, grant: true);
        Grant(file, 0x120089); // Only this model object is readable, with no execute/write grant.
    }

    private void PinAncestors(string path)
    {
        var parents = new Stack<string>();
        var parent = Path.GetDirectoryName(path);
        while (parent is not null)
        {
            parents.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }
        while (parents.TryPop(out var ancestor)) Pin(ancestor, directory: true, grant: false);
    }

    private SafeFileHandle Pin(string path, bool directory, bool grant)
    {
        if (_pinned.TryGetValue(path, out var existing))
        {
            // Runtime/model files are distinct. Only ancestor pins may be reused.
            if (grant) throw new IOException("Runtime and model access must have distinct scopes.");
            return existing;
        }
        // OPEN_REPARSE_POINT checks the actual directory entry. No share-delete prevents replacing
        // any ancestor. The selected code/model additionally denies write sharing for its lifetime.
        var access = 0x80u | (grant ? 0x60001u : 0u);
        var share = grant ? 1u : 3u;
        var handle = CreateFileW(path, access, share, 0, 3, 0x00200000 | 0x02000000, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Error("Cannot hold the model/runtime path safely");
        }
        _pins.Add(handle);
        Check(GetFileInformationByHandle(handle, out var info), "Cannot verify a model/runtime path");
        if ((info.Attributes & UnsafeAttributes) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new IOException("The model and runtime must be regular files and directories with no redirection.");
        _pinned.Add(path, handle);
        return handle;
    }

    private void Grant(SafeFileHandle handle, int mask)
    {
        using var guard = AclGuard.Acquire();
        var acl = ReadAcl(handle);
        for (var i = 0; i < acl.Count; i++)
            if (acl[i] is KnownAce existing && existing.SecurityIdentifier == _sid)
                throw new IOException("An unexpected model sandbox permission already exists.");
        var position = acl.Count;
        for (var i = 0; i < acl.Count; i++)
            if ((acl[i].AceFlags & AceFlags.Inherited) != 0) { position = i; break; }
        acl.InsertAce(position, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, mask, _sid, false, null));
        WriteAcl(handle, acl);
        _grants.Add(handle);
    }

    private static RawAcl ReadAcl(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 4, out _, out _, out var dacl, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "Cannot inspect model sandbox file permissions");
        try
        {
            if (dacl == 0) throw new IOException("Unrestricted file permissions cannot be used for a model sandbox.");
            var bytes = new byte[(ushort)Marshal.ReadInt16(dacl, 2)];
            Marshal.Copy(dacl, bytes, 0, bytes.Length);
            return new RawAcl(bytes, 0);
        }
        finally { LocalFree(descriptor); }
    }

    private static void WriteAcl(SafeFileHandle handle, RawAcl acl)
    {
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var error = SetSecurityInfo(handle, 1, 4, 0, 0, buffer, 0);
            if (error != 0) throw new Win32Exception((int)error, "Cannot set model sandbox file permissions");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        try
        {
            using var guard = AclGuard.Acquire();
            foreach (var handle in _grants.AsEnumerable().Reverse())
            {
                try
                {
                    var acl = ReadAcl(handle);
                    var changed = false;
                    for (var i = acl.Count - 1; i >= 0; i--)
                        if (acl[i] is KnownAce ace && ace.SecurityIdentifier == _sid)
                        {
                            acl.RemoveAce(i);
                            changed = true;
                        }
                    if (changed) WriteAcl(handle, acl);
                }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            }
        }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        finally { foreach (var handle in _pins.AsEnumerable().Reverse()) handle.Dispose(); }
        if (errors is not null) throw new AggregateException("Model sandbox permission cleanup failed.", errors);
    }

    private sealed class AclGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private AclGuard(Mutex mutex) => _mutex = mutex;
        internal static AclGuard Acquire()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User?.Value
                ?? throw new IOException("Cannot identify the model sandbox owner.");
            var mutex = new Mutex(false, "Local\\SpaceMaid.ModelSandboxAcl." + userSid);
            try
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                        throw new IOException("Model sandbox permissions are busy. Try again.");
                }
                catch (AbandonedMutexException) { /* We now own it; reread the current ACL. */ }
                return new AclGuard(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }
        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }
}
