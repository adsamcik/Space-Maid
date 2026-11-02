using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Media.Imaging;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App.Extensions;

internal sealed partial class ExtensionRuntime
{
    internal static BitmapSource? ReadNavigationIcon(DiskAccess files, string directory, string? relativePath,
        Action<string>? unavailable = null)
    {
        if (relativePath is null) return null;
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 || Path.IsPathRooted(relativePath)
                || !relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Navigation icons must be package-relative PNG files.");
            var segments = relativePath.Split(['\\', '/']);
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Navigation icon paths cannot leave their extension package.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (root.StartsWith('\\') || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Navigation icons must remain inside a local extension package.");
            const FileAttributes unavailableAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline
                | (FileAttributes)(0x40000 | 0x400000); // RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS.
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                var attributes = files.GetAttributes(current);
                bool expectedDirectory = current != path;
                if ((attributes & unavailableAttributes) != 0 || ((attributes & FileAttributes.Directory) != 0) != expectedDirectory)
                    throw new InvalidDataException("Linked, offline or unavailable navigation icons are not loaded.");
            }
            using var input = files.OpenRead(path);
            if (input.Length is < 33 or > 1024 * 1024)
                throw new InvalidDataException("Navigation icons must be PNG files no larger than 1 MiB.");
            Span<byte> header = stackalloc byte[24]; input.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 || !header[12..16].SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) is < 1 or > 1024
                || BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) is < 1 or > 1024)
                throw new InvalidDataException("Navigation icon dimensions must be between 1 and 1024 pixels.");
            input.Position = 0;
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var icon = decoder.Frames.Single();
            if (icon.PixelWidth is < 1 or > 1024 || icon.PixelHeight is < 1 or > 1024 || !icon.CanFreeze)
                throw new InvalidDataException("Navigation icon dimensions or format are unsupported.");
            icon.Freeze();
            return icon;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or FormatException or Win32Exception or SecurityException)
        {
            unavailable?.Invoke(error.GetBaseException().Message);
            return null;
        }
    }
}
