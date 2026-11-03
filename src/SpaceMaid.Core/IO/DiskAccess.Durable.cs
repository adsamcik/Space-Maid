using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    // Write-ahead cleanup records must reach the file before an irreversible
    // mutation is admitted. A failed write never replaces the previous record.
    public void WriteAllTextAtomicDurable(string path, string contents)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Flush the namespace change too; a buffered rename is not enough
            // for a manifest that must precede irreversible cleanup.
            if (!DurableFileNative.MoveFileEx(NativeMethods.ExtendedPath(Path.GetFullPath(temporary)),
                NativeMethods.ExtendedPath(Path.GetFullPath(path)), 0x1 | 0x8))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cleanup data could not be committed to disk.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void AppendAllTextDurable(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(Encoding.UTF8.GetBytes(contents));
        stream.Flush(flushToDisk: true);
    }

    private static class DurableFileNative
    {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string source, string destination, uint flags);
    }
}
