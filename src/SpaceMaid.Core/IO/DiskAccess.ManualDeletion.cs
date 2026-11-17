using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    internal void DeleteUserConfirmedFolder(string path, FileIdentity identity, ulong volume, CancellationToken token,
        Action? mutationStarted)
    {
        token.ThrowIfCancellationRequested();
        var leases = new Stack<SafeFileHandle>();
        var folders = new Stack<(SafeFileHandle Handle, FileAttributes Attributes, IEnumerator<DirectoryMember> Children)>();
        try
        {
            // Beyond the drive root, resolve one name at a time against a held
            // directory. An ancestor's path is never resolved again.
            string drive = Path.GetPathRoot(path)!;
            string[] names = path[drive.Length..].Split(Path.DirectorySeparatorChar);
            using var driveHandle = OpenPermanentDeleteHandle(drive, 0x81, 3); // LIST_DIRECTORY | READ_ATTRIBUTES
            var driveMetadata = InspectPermanentDeleteHandle(driveHandle);
            CheckPermanentDeleteAttributes(driveMetadata.Attributes, directory: true);
            if (driveMetadata.Volume != volume) throw new IOException("The confirmed folder's drive has changed.");
            var parent = driveHandle;
            foreach (string name in names.AsSpan(0, names.Length - 1))
            {
                token.ThrowIfCancellationRequested();
                var lease = OpenDeletionChild(parent, name, 0x81, ignoreCase: true);
                leases.Push(lease);
                var metadata = InspectPermanentDeleteHandle(lease);
                CheckPermanentDeleteAttributes(metadata.Attributes, directory: true);
                if (metadata.Volume != volume) throw new IOException("The confirmed folder's drive has changed.");
                parent = lease;
            }

            OpenEntry(parent, new(names[^1], FileAttributes.Directory), isRoot: true);
            while (folders.TryPeek(out var folder))
            {
                token.ThrowIfCancellationRequested();
                if (folder.Children.MoveNext())
                {
                    OpenEntry(folder.Handle, folder.Children.Current, isRoot: false);
                    continue;
                }
                folder.Children.Dispose();
                DeleteEntry(folder.Handle, folder.Attributes);
                folders.Pop();
                folder.Handle.Dispose();
            }

            try { GetAttributes(NativeMethods.ExtendedPath(path)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            throw new IOException("The original folder path is still present. Recheck the location.");
        }
        finally
        {
            while (folders.TryPop(out var folder))
            {
                folder.Children.Dispose();
                folder.Handle.Dispose();
            }
            while (leases.TryPop(out var lease)) lease.Dispose();
        }

        void OpenEntry(SafeFileHandle parent, DirectoryMember member, bool isRoot)
        {
            token.ThrowIfCancellationRequested();
            const uint deleteAndReadAttributes = 0x10080;
            const uint listDirectory = 1;
            bool descend = (member.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                == FileAttributes.Directory;
            var handle = OpenDeletionChild(parent, member.Name, deleteAndReadAttributes | (descend ? listDirectory : 0),
                ignoreCase: isRoot);
            bool held = false;
            try
            {
                var entry = InspectPermanentDeleteHandle(handle);
                if (entry.Volume != volume) throw new IOException("An entry is on a different drive.");
                bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
                bool reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (isRoot && (!directory || reparse || entry.Identity != identity))
                    throw new IOException("This folder changed after confirmation. Recheck it before deleting.");
                if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    != (member.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)))
                    throw new IOException("An entry changed while deleting the folder. Recheck the location.");
                if (directory && !reparse)
                {
                    CheckPermanentDeleteAttributes(entry.Attributes, directory: true);
                    folders.Push((handle, entry.Attributes, ReadDeletionDirectory(handle).GetEnumerator()));
                    held = true;
                }
                else DeleteEntry(handle, entry.Attributes); // Delete links themselves without traversing their targets.
            }
            finally { if (!held) handle.Dispose(); }
        }

        void DeleteEntry(SafeFileHandle handle, FileAttributes attributes)
        {
            token.ThrowIfCancellationRequested();
            mutationStarted?.Invoke();
            mutationStarted = null;
            if ((attributes & FileAttributes.ReadOnly) != 0)
                MarkReadOnlyEntryForDeletion(handle);
            else MarkPermanentDelete(handle);
        }
    }

    private IEnumerable<DirectoryMember> ReadDeletionDirectory(SafeFileHandle handle)
    {
        const int fileFullDirectoryInfo = 14;
        var buffer = new byte[4096];
        while (ReadHandleInformation(handle, fileFullDirectoryInfo, buffer, buffer.Length))
        {
            int offset = 0;
            do
            {
                var member = ReadDeletionMember(buffer, ref offset);
                if (member.Name is not ("." or "..")) yield return member;
            }
            while (offset != 0);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.NoMoreFiles) throw new Win32Exception(error);
    }

    private static DirectoryMember ReadDeletionMember(byte[] buffer, ref int offset)
    {
        // FILE_FULL_DIR_INFO ends with a variable-length UTF-16 name at byte 68.
        const int nameOffset = 68;
        var item = buffer.AsSpan(offset);
        uint next = LayoutParser.U32(item, 0);
        uint length = LayoutParser.U32(item, 60);
        if (length == 0 || (length & 1) != 0 || length > item.Length - nameOffset
            || next != 0 && (next < nameOffset + length || next > item.Length - nameOffset))
            throw new IOException("Windows returned an invalid directory entry.");
        var name = new string(MemoryMarshal.Cast<byte, char>(item.Slice(nameOffset, (int)length)));
        var attributes = (FileAttributes)LayoutParser.U32(item, 56);
        offset = next == 0 ? 0 : offset + (int)next;
        return new(name, attributes);
    }

    private static unsafe SafeFileHandle OpenDeletionChild(SafeFileHandle parent, string name, uint access, bool ignoreCase)
    {
        if (name.Length == 0 || name is "." or ".." || name.AsSpan().ContainsAny('\\', '/', ':') || name.Contains('\0'))
            throw new IOException("An invalid child name prevents permanent deletion.");
        const uint synchronousIoNonAlert = 0x20, fileOpen = 1, openNoRecall = 0x00400000;
        const uint synchronize = 0x00100000, shareReadWrite = 3;
        const uint objectDontReparse = 0x1000, objectCaseInsensitive = 0x40;
        ushort bytes = checked((ushort)(name.Length * sizeof(char)));
        fixed (char* characters = name)
        {
            var component = new DeletionUnicodeString { Length = bytes, MaximumLength = bytes, Buffer = characters };
            var attributes = new DeletionObjectAttributes
            {
                Length = (uint)sizeof(DeletionObjectAttributes), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &component, Attributes = objectDontReparse | (ignoreCase ? objectCaseInsensitive : 0)
            };
            nint* statusBlock = stackalloc nint[2];
            // FILE_OPEN_NO_RECALL cannot be combined with FILE_DIRECTORY_FILE.
            // Callers verify the opened handle's type before enumerating it.
            int status = NtCreateFile(out var handle, access | synchronize, &attributes, statusBlock, 0, 0,
                shareReadWrite, fileOpen, synchronousIoNonAlert | NativeMethods.OpenReparsePoint | openNoRecall, 0, 0);
            GC.KeepAlive(parent);
            if (status >= 0) return handle;
            handle.Dispose();
            throw new Win32Exception((int)RtlNtStatusToDosError(status));
        }
    }

    private unsafe void MarkReadOnlyEntryForDeletion(SafeFileHandle handle)
    {
        // Keep the attribute on any other hard links to the same file.
        const uint deleteIgnoringReadOnly = 0x01 | 0x10;
        uint flags = deleteIgnoringReadOnly;
        if (!WriteHandleInformation(handle, 21, &flags, sizeof(uint))) // FileDispositionInfoEx
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private readonly record struct DirectoryMember(string Name, FileAttributes Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DeletionUnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DeletionObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public DeletionUnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern unsafe int NtCreateFile(out SafeFileHandle handle, uint access, DeletionObjectAttributes* attributes,
        nint* ioStatus, nint size, uint fileAttributes, uint sharing, uint disposition, uint options, nint eaBuffer, uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
