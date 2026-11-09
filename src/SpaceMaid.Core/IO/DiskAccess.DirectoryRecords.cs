using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    internal const int DirectoryRecordBufferSize = 128 * 1024;

    internal int ReadDirectoryRecords(SafeFileHandle directory, byte[] buffer, bool extended = true) =>
        QueryDirectoryRecords(directory, buffer, extended ? 19 : 14);

    internal int ReadFirstDirectoryRecords(SafeFileHandle directory, byte[] buffer, out bool extended)
    {
        int error = ReadDirectoryRecords(directory, buffer);
        extended = error is not (1 or 50 or 87);
        // Unsupported extended metadata can fall back on the same held handle.
        // Access failures and a failed query partway through a listing cannot.
        return extended ? error : QueryDirectoryRecords(directory, buffer, 15); // FileFullDirectoryRestartInfo
    }

    private int QueryDirectoryRecords(SafeFileHandle directory, byte[] buffer, int kind) =>
        ReadHandleInformation(directory, kind, buffer, buffer.Length) ? 0 : Marshal.GetLastPInvokeError();

    // FILE_ID_EXTD_DIR_INFO or FILE_FULL_DIR_INFO. The scanner can read names directly from the
    // borrowed buffer without allocating strings for every file.
    internal readonly ref struct DirectoryRecord
    {
        private readonly ReadOnlySpan<byte> _header;
        private readonly bool _extended;
        public ReadOnlySpan<char> Name { get; }
        public FileAttributes Attributes => (FileAttributes)LayoutParser.U32(_header, 56);
        public uint Tag => _extended ? LayoutParser.U32(_header, 68)
            : (Attributes & FileAttributes.ReparsePoint) != 0 ? LayoutParser.U32(_header, 64) : 0; // EaSize holds the tag
        public FileIdentity Identity => _extended ? new(LayoutParser.U64(_header, 72), LayoutParser.U64(_header, 80)) : default;
        public long LogicalBytes => LayoutParser.I64(_header, 40);
        public long AllocatedBytes => LayoutParser.I64(_header, 48);
        public long LastWrite => LayoutParser.I64(_header, 24);
        public long Changed => LayoutParser.I64(_header, 32);
        public long Created => LayoutParser.I64(_header, 8);

        public DirectoryRecord(ReadOnlySpan<byte> batch, ref int offset, bool extended = true)
        {
            int headerSize = extended ? 88 : 68;
            _extended = extended;
            LayoutParser.Require(batch, offset, headerSize);
            _header = batch.Slice(offset, headerSize);
            int next = checked((int)LayoutParser.U32(_header, 0));
            int length = next == 0 ? batch.Length - offset : next;
            if (length < headerSize || length > batch.Length - offset)
                throw new InvalidDataException("Invalid directory metadata.");
            int nameBytes = checked((int)LayoutParser.U32(_header, 60));
            if ((nameBytes & 1) != 0 || nameBytes > length - headerSize)
                throw new InvalidDataException("Invalid filename length.");
            Name = MemoryMarshal.Cast<byte, char>(batch.Slice(offset + headerSize, nameBytes));
            offset = next == 0 ? -1 : offset + next;
        }
    }
}
