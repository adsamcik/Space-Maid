using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Core.IO;

public readonly record struct DiskFileMetadata(string FullName, long Length, DateTime LastWriteTimeUtc);

// Keep lazy drive probes (especially offline network drives) inside the boundary.
public sealed class DiskDrive
{
    private readonly DriveInfo _drive;
    internal DiskDrive(DriveInfo drive) => _drive = drive;
    public string Name => _drive.Name;
    public bool IsReady => _drive.IsReady;
    public DriveType DriveType => _drive.DriveType;
    public string DriveFormat => _drive.DriveFormat;
    public string VolumeLabel => _drive.VolumeLabel;
    public long TotalSize => _drive.TotalSize;
    public long AvailableFreeSpace => _drive.AvailableFreeSpace;
}

public sealed partial class DiskAccess
{
    // Content, namespace mutations and path probes use normal filesystem APIs.
    // NTFS layout queries cannot replace their sharing, creation or error semantics.
    public bool FileExists(string? path) => File.Exists(path);
    public bool DirectoryExists(string? path) => Directory.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public long FileLength(string path) => new FileInfo(path).Length;
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string? contents) => File.WriteAllText(path, contents);
    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken token = default) => File.WriteAllBytesAsync(path, bytes, token);
    public FileStream OpenRead(string path) => File.OpenRead(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void MoveFile(string source, string destination, bool overwrite = false) => File.Move(source, destination, overwrite);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive = false) => Directory.Delete(path, recursive);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void SetLastWriteTimeUtc(string path, DateTime time) => File.SetLastWriteTimeUtc(path, time);
    public void SetLastWriteTimeUtc(SafeFileHandle handle, DateTime time) => File.SetLastWriteTimeUtc(handle, time);
    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*") => Directory.EnumerateFiles(path, pattern);
    public IEnumerable<string> EnumerateFiles(string path, string pattern, EnumerationOptions options) => Directory.EnumerateFiles(path, pattern, options);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, EnumerationOptions options) => Directory.EnumerateDirectories(path, pattern, options);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public IEnumerable<DiskFileMetadata> EnumerateFileMetadata(string path, string pattern)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles(pattern))
            yield return new(file.FullName, file.Length, file.LastWriteTimeUtc);
    }
    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share,
        int bufferSize = 4096, bool useAsync = false) => new(path, mode, access, share, bufferSize, useAsync);
    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share,
        int bufferSize, FileOptions options) => new(path, mode, access, share, bufferSize, options);
    public FileStream Open(SafeFileHandle handle, FileAccess access, int bufferSize = 4096,
        bool isAsync = false) => new(handle, access, bufferSize, isAsync);
    public DiskDrive GetDrive(string root) => new(new DriveInfo(root));
    public IReadOnlyList<DiskDrive> GetDrives() => DriveInfo.GetDrives().Select(drive => new DiskDrive(drive)).ToArray();
    public int Read(SafeFileHandle handle, Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    public string? VolumeId(string root)
    {
        var name = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(root, name, name.Capacity) ? name.ToString() : null;
    }

    public bool CanUseNtfs(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))!;
        if (root.Length != 3 || !char.IsAsciiLetter(root[0])) return false;
        try
        {
            var drive = GetDrive(root);
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable
                && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    // Native safety providers keep their exact access/sharing/flags and error
    // handling. An open is never retried with weaker flags or by a different path.
    public SafeFileHandle OpenHandle(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template) => CreateFile(name, access, share, security, creation, flags, template);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
