namespace SpaceMaid.Core.Scanning;

internal static partial class NativeMethods
{
    // winioctl.h: CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 157, METHOD_NEITHER, FILE_ANY_ACCESS).
    public const uint QueryFileLayout = (9u << 16) | (157u << 2) | 3u;
    public const uint BackupSemantics = 0x02000000;
    public const uint OpenReparsePoint = 0x00200000;
    public const int NoMoreFiles = 18;
    public const int HandleEof = 38;

    public static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
