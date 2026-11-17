using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.IO;

namespace SpaceMaid.Extensions.Photos;

// Attribute-only directory leases validate and pin the user's chosen namespace
// while the catalog changes. No source content or mutation access is requested.
internal sealed class PhotoCollectionRoot : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    public string Path { get; }
    public PhotoCollectionRoot(string path, DiskAccess files, CancellationToken token)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose an available local photo folder.");
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        try
        {
            var ancestors = new Stack<string>();
            for (string? ancestorPath = Path; ancestorPath is not null; ancestorPath = System.IO.Path.GetDirectoryName(ancestorPath)) ancestors.Push(ancestorPath);
            while (ancestors.TryPop(out var directoryPath))
            {
                token.ThrowIfCancellationRequested();
                var handle = files.OpenHandle(directoryPath, 0x80, 3, 0, 3, 0x02300000, 0);
                _handles.Add(handle);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var info = new byte[40];
                if (!files.ReadHandleInformation(handle, 0, info, info.Length)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32));
                if ((attributes & 0x10) == 0 || (attributes & (0x400 | 0x1000 | 0x40000 | 0x400000)) != 0)
                    throw new IOException("Choose a directly accessible folder. Linked or offline locations cannot replace this collection's root.");
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
}
