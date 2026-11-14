using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed record RecyclePermissionTarget(string Path, FileIdentity Identity, ulong Volume, long Created, bool IsDirectory);

public sealed partial class DiskAccess
{
    private const uint ReadPermissions = 0x20000;
    private const uint WritePermissions = 0x40000;
    private const uint WriteOwner = 0x80000;
    private const int DeleteAccess = 0x10000;
    private const uint OwnerAndDacl = 5;
    private const FileAttributes RepairUnsafe = FileAttributes.ReparsePoint | FileAttributes.Offline
        | FileAttributes.System | (FileAttributes)0x440000;
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public RecyclePermissionTarget CaptureRecyclePermissionTarget(FolderDeletionIdentity folder, string path)
    {
        path = ValidateRepairPath(folder, path);
        using var ancestors = PinRepairAncestors(path);
        using var target = OpenPermanentDeleteHandle(path, 0x80, 7);
        return InspectRepairTarget(folder, path, target);
    }

    internal void RepairRecyclePermission(FolderDeletionIdentity folder, RecyclePermissionTarget confirmed,
        SecurityIdentifier user, CancellationToken token)
    {
        string path = ValidateRepairPath(folder, confirmed.Path);
        token.ThrowIfCancellationRequested();
        using var ancestors = PinRepairAncestors(path);
        // Reading/listing access makes the no-delete sharing lease effective.
        // The helper enables backup and restore privileges for this one open.
        using var handle = OpenPermanentDeleteHandle(path, ReadPermissions | WritePermissions | WriteOwner | 0x81, 3);
        if (InspectRepairTarget(folder, path, handle) != confirmed)
            throw new IOException("The item changed since confirmation. Its permissions were kept.");
        var descriptor = ReadPermissionDescriptor(handle);
        if (descriptor.Owner is null || descriptor.Owner.Equals(TrustedInstaller))
            throw new IOException("Windows-protected ownership cannot be changed here.");
        var updated = AddRecycleAccess(descriptor, user);
        var check = ReadPermissionDescriptor(handle);
        if (!GetDescriptorBytes(check).AsSpan().SequenceEqual(GetDescriptorBytes(descriptor)))
            throw new IOException("The item's permissions changed while checking. Review it again.");
        using var live = OpenPermanentDeleteHandle(path, 0x80, 7);
        if (InspectRepairTarget(folder, path, live) != confirmed)
            throw new IOException("The item changed since confirmation. Its permissions were kept.");
        token.ThrowIfCancellationRequested();
        // Native security assignment targets this handle only. SetSecurityInfo
        // may propagate directory ACLs to descendants, which were not approved.
        uint fields = updated.DiscretionaryAcl is null ? 1 : OwnerAndDacl;
        if ((updated.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 && fields == OwnerAndDacl)
            fields |= 0x80000000; // PROTECTED_DACL_SECURITY_INFORMATION
        int status = NtSetSecurityObject(handle, fields, GetDescriptorBytes(updated));
        if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
    }

    private static RawSecurityDescriptor AddRecycleAccess(RawSecurityDescriptor source, SecurityIdentifier user)
    {
        var result = new RawSecurityDescriptor(GetDescriptorBytes(source), 0) { Owner = user };
        if (result.DiscretionaryAcl is { } acl)
        {
            // Keep deny rules intact, including inherited and group rules.
            // Any delete-denying rule can affect the account under review.
            foreach (GenericAce ace in acl)
                if (ace is QualifiedAce { AceQualifier: AceQualifier.AccessDenied } deny
                    && (deny.AceFlags & AceFlags.InheritOnly) == 0
                    && (deny.AccessMask & (DeleteAccess | 0x10000000)) != 0)
                    throw new IOException("An existing rule denies delete access. Ownership and permissions were kept.");
            int index = 0;
            while (index < acl.Count && (acl[index].AceFlags & AceFlags.Inherited) == 0) index++;
            acl.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, DeleteAccess, user, false, null));
        }
        return result;
    }

    private static byte[] GetDescriptorBytes(RawSecurityDescriptor descriptor)
    {
        var data = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(data, 0);
        return data;
    }

    private static RawSecurityDescriptor ReadPermissionDescriptor(SafeFileHandle handle)
    {
        int error = GetSecurityInfo(handle, 1, OwnerAndDacl, 0, 0, 0, 0, out nint descriptor);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new(bytes, 0);
        }
        finally { LocalFree(descriptor); }
    }

    private string ValidateRepairPath(FolderDeletionIdentity folder, string path)
    {
        string root = FolderDeletionTarget.ValidatePath(folder.Path);
        string item = FolderDeletionTarget.ValidatePath(path);
        if ((!item.Equals(root, StringComparison.Ordinal) && !item.StartsWith(root + "\\", StringComparison.Ordinal))
            || item.Length > 4096 || !CanUseNtfs(item))
            throw new IOException("Permission repair requires an exact item in the confirmed local NTFS folder.");
        string volume = Path.GetPathRoot(item)!;
        string[] segments = item[volume.Length..].Split('\\');
        if (segments.Any(segment => segment.StartsWith('$') || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            || new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "Boot", "EFI" }
                .Contains(segments[0], StringComparer.OrdinalIgnoreCase)
            || IsProtectedRepairPath(item, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            || IsProtectedRepairPath(item, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsProtectedRepairPath(item, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
            || IsProtectedRepairPath(item, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
            || IsProtectedRepairPath(item, AppContext.BaseDirectory)
            || Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Equals("ScanHelper", StringComparison.OrdinalIgnoreCase)
                && IsProtectedRepairPath(item, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))))
            throw new IOException("Permission repair is unavailable for system or application files.");
        return item;
    }

    private static bool IsProtectedRepairPath(string item, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        root = Path.TrimEndingDirectorySeparator(root);
        return item.Equals(root, StringComparison.OrdinalIgnoreCase) || item.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(item + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private RepairAncestorLease PinRepairAncestors(string path)
    {
        var pending = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            pending.Push(parent);
        var lease = new RepairAncestorLease();
        try
        {
            foreach (string parent in pending)
            {
                using var metadata = OpenPermanentDeleteHandle(parent, 0x80, 7);
                var before = InspectPermanentDeleteHandle(metadata);
                CheckRepairAttributes(before.Attributes, directory: true, ancestor: true);
                var handle = OpenPermanentDeleteHandle(parent, 0x81, 3);
                lease.Handles.Add(handle);
                var after = InspectPermanentDeleteHandle(handle);
                CheckRepairAttributes(after.Attributes, directory: true, ancestor: true);
                if (before.Identity != after.Identity || before.Volume != after.Volume)
                    throw new IOException("A parent folder changed while checking the item.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private unsafe RecyclePermissionTarget InspectRepairTarget(FolderDeletionIdentity folder, string path, SafeFileHandle handle)
    {
        FolderDeletionTarget.RestoreCapturedIdentity(folder, this);
        var metadata = InspectPermanentDeleteHandle(handle);
        bool directory = (metadata.Attributes & FileAttributes.Directory) != 0;
        CheckRepairAttributes(metadata.Attributes, directory, ancestor: false);
        if (metadata.Identity == default || metadata.Volume != folder.VolumeSerialNumber
            || path == folder.Path && (!directory || metadata.Identity != folder.Identity))
            throw new IOException("The item changed since confirmation.");
        ValidateRepairHandlePath(path, handle);
        if (!directory)
        {
            byte* info = stackalloc byte[24];
            ReadPermanentDeleteInformation(handle, 1, info, 24);
            if (LayoutParser.U32(new ReadOnlySpan<byte>(info, 24), 16) != 1)
                throw new IOException("Permission repair is unavailable for files with multiple hard links.");
        }
        return new(path, metadata.Identity, metadata.Volume, metadata.Created, directory);
    }

    private static void ValidateRepairHandlePath(string path, SafeFileHandle handle)
    {
        var final = new StringBuilder(path.Length + 32);
        uint size = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        string resolved = final.ToString();
        if (size == 0 || size >= final.Capacity ||
            !string.Equals(NativeMethods.ExtendedPath(path), resolved, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The item resolves through a different Windows path. Its permissions were kept.");
    }

    private static void CheckRepairAttributes(FileAttributes attributes, bool directory, bool ancestor)
    {
        if (((attributes & FileAttributes.Directory) != 0) != directory ||
            (attributes & (ancestor ? RepairUnsafe & ~FileAttributes.System : RepairUnsafe)) != 0)
            throw new IOException("Permission repair is unavailable for linked, offline or protected items.");
    }

    private sealed class RepairAncestorLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(SafeFileHandle handle, uint type, uint info, nint owner, nint group,
        nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint pointer);
    [DllImport("ntdll.dll")]
    private static extern int NtSetSecurityObject(SafeFileHandle handle, uint securityInformation, byte[] descriptor);
}
