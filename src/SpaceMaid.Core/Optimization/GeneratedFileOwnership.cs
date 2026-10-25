using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

public sealed record GeneratedOwnershipRecord(int Version, string Owner, string FileName, string Sha256,
    IReadOnlyList<GeneratedInputRevision> Inputs, DateTime ProducedAtUtc)
{
    public GeneratedInputRevision? Output { get; init; }
}
public sealed record GeneratedProducedFile(GeneratedInputRevision Revision, string Sha256);
public sealed record GeneratedRemovalResult(bool OutputRemoved, bool ReceiptRemoved, long RemovedOutputBytes, string? Reason = null);

// Producers call this only after successfully writing their generated output.
// The receipt proves exact bytes and source revisions, not a whole directory.
public static partial class GeneratedFileOwnership
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024, MaximumReceiptBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string ReceiptPath(string generatedFile) => generatedFile + ".spacemaid-owner.json";

    // Capture while the producer still holds its exclusive, readable write handle.
    public static GeneratedProducedFile CaptureProduced(FileStream stream, string path)
    {
        stream.Flush();
        var revision = GeneratedReadLease.Inspect(stream.SafeFileHandle, OptimizationPolicy.NormalizePath(path));
        return new(revision, Hash(stream, MaximumOutputBytes));
    }

    public static void Record(string generatedFile, string owner, IReadOnlyList<string> inputs, GeneratedProducedFile? produced = null)
    {
        generatedFile = OptimizationPolicy.NormalizePath(generatedFile);
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || inputs.Count is < 1 or > 14) throw new ArgumentException("An owner and bounded source inputs are required.");
        using var output = GeneratedReadLease.Open(generatedFile);
        CheckSingleStream(output.Stream.SafeFileHandle);
        string hash = Hash(output.Stream, MaximumOutputBytes); output.Verify();
        if (produced is not null && (!SameObject(produced.Revision, output.Revision) || produced.Sha256 != hash))
            throw new IOException("The producer's output changed before publication.");
        var sources = new List<GeneratedInputRevision>();
        foreach (string input in inputs)
        { using var source = GeneratedReadLease.Open(input); source.Verify(); sources.Add(source.Revision); }
        var receipt = new GeneratedOwnershipRecord(2, owner, Path.GetFileName(generatedFile), hash, sources.AsReadOnly(), DateTime.UtcNow) { Output = output.Revision };
        string path = ReceiptPath(generatedFile), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        GeneratedProducedFile? written = null;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
            if (bytes.Length > MaximumReceiptBytes) throw new IOException("The ownership receipt exceeds its metadata budget.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { stream.Write(bytes); written = CaptureProduced(stream, temporary); }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (written is not null) TryDeleteProduced(temporary, written); }
    }

    public static GeneratedOwnedReadLease OpenOwned(string generatedFile, string owner, IReadOnlyList<string>? requiredInputs = null)
    {
        generatedFile = OptimizationPolicy.NormalizePath(generatedFile);
        var leases = new List<GeneratedReadLease>();
        try
        {
            var receipt = GeneratedReadLease.Open(ReceiptPath(generatedFile)); leases.Add(receipt);
            var record = ReadRecord(receipt.Stream, generatedFile, owner);
            if (requiredInputs is not null && !record.Inputs.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(requiredInputs.Select(OptimizationPolicy.NormalizePath))) throw new IOException("The cache belongs to different source inputs.");
            OpenInputs(record, leases);
            var output = GeneratedReadLease.Open(generatedFile); leases.Add(output);
            ValidateOutput(record, output.Revision, output.Stream);
            foreach (var lease in leases) lease.Verify();
            return new(output.Stream, leases);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }

    public static GeneratedRemovalResult TryDeleteOwned(string generatedFile, string owner, CancellationToken token = default)
    {
        generatedFile = OptimizationPolicy.NormalizePath(generatedFile);
        var inputs = new List<GeneratedReadLease>();
        try
        {
            GeneratedInputRevision receiptRevision; long bytes;
            using (var receipt = GeneratedReadLease.Open(ReceiptPath(generatedFile)))
            {
                var record = ReadRecord(receipt.Stream, generatedFile, owner); OpenInputs(record, inputs);
                using var output = DeleteLease.Open(generatedFile);
                ValidateOutput(record, output.Revision, output.Stream); receipt.Verify();
                foreach (var input in inputs) input.Verify();
                receiptRevision = receipt.Revision; bytes = output.Revision.Length;
                token.ThrowIfCancellationRequested(); output.Delete();
            }
            return new(true, TryRemoveReceiptAfterDeletion(generatedFile, owner, receiptRevision), bytes);
        }
        catch (Exception error) when (Expected(error)) { return new(false, false, 0, error.Message); }
        finally { foreach (var input in inputs.AsEnumerable().Reverse()) input.Dispose(); }
    }

    public static bool TryTouchOwned(string generatedFile, string owner)
    {
        generatedFile = OptimizationPolicy.NormalizePath(generatedFile);
        var inputs = new List<GeneratedReadLease>();
        try
        {
            using var receipt = GeneratedReadLease.Open(ReceiptPath(generatedFile));
            var record = ReadRecord(receipt.Stream, generatedFile, owner); OpenInputs(record, inputs);
            using var output = DeleteLease.Open(generatedFile);
            ValidateOutput(record, output.Revision, output.Stream); receipt.Verify();
            foreach (var input in inputs) input.Verify();
            File.SetLastWriteTimeUtc(output.Stream.SafeFileHandle, DateTime.UtcNow); return true;
        }
        catch (Exception error) when (Expected(error)) { return false; }
        finally { foreach (var input in inputs.AsEnumerable().Reverse()) input.Dispose(); }
    }

    // The optimizer passes the receipt revision captured in its approved proof.
    public static bool TryRemoveReceiptAfterDeletion(string generatedFile, string owner, GeneratedInputRevision expectedReceipt, CancellationToken token = default)
    {
        generatedFile = OptimizationPolicy.NormalizePath(generatedFile);
        try
        {
            if (!expectedReceipt.Path.Equals(ReceiptPath(generatedFile), StringComparison.OrdinalIgnoreCase)) return false;
            using var receipt = DeleteLease.Open(expectedReceipt.Path);
            if (receipt.Revision != expectedReceipt) return false;
            _ = ReadRecord(receipt.Stream, generatedFile, owner);
            if (Present(generatedFile)) return false;
            token.ThrowIfCancellationRequested(); receipt.Delete(); return true;
        }
        catch (Exception error) when (Expected(error)) { return false; }
    }

    public static bool TryRemoveOrphanReceipt(string generatedFile, string owner)
    {
        try
        {
            GeneratedInputRevision revision;
            using (var receipt = GeneratedReadLease.Open(ReceiptPath(generatedFile)))
            { _ = ReadRecord(receipt.Stream, OptimizationPolicy.NormalizePath(generatedFile), owner); receipt.Verify(); revision = receipt.Revision; }
            return TryRemoveReceiptAfterDeletion(generatedFile, owner, revision);
        }
        catch (Exception error) when (Expected(error)) { return false; }
    }

    public static bool TryDeleteProduced(string path, GeneratedProducedFile produced)
    {
        try
        {
            using var file = DeleteLease.Open(OptimizationPolicy.NormalizePath(path));
            if (!file.Revision.Path.Equals(produced.Revision.Path, StringComparison.OrdinalIgnoreCase)
                || !SameObject(file.Revision, produced.Revision) || Hash(file.Stream, MaximumOutputBytes) != produced.Sha256) return false;
            file.Delete(); return true;
        }
        catch (Exception error) when (Expected(error)) { return false; }
    }

    private static void OpenInputs(GeneratedOwnershipRecord record, List<GeneratedReadLease> leases)
    {
        foreach (var expected in record.Inputs)
        {
            var input = GeneratedReadLease.Open(expected.Path); leases.Add(input);
            if (input.Revision != expected) throw new IOException("The source revision changed; the generated file is kept.");
        }
    }
    private static void ValidateOutput(GeneratedOwnershipRecord record, GeneratedInputRevision revision, FileStream stream)
    {
        CheckSingleStream(stream.SafeFileHandle);
        if (record.Output is null || !SameObject(record.Output, revision) || Hash(stream, MaximumOutputBytes) != record.Sha256)
            throw new IOException("Current native output identity and owned bytes are required.");
    }
    private static bool SameObject(GeneratedInputRevision expected, GeneratedInputRevision actual) => expected.Volume == actual.Volume
        && expected.Identity == actual.Identity && expected.Length == actual.Length;
    private static GeneratedOwnershipRecord ReadRecord(FileStream stream, string path, string owner)
    {
        CheckSingleStream(stream.SafeFileHandle);
        if (stream.Length is <= 0 or > MaximumReceiptBytes) throw new IOException("The ownership receipt exceeds its metadata budget.");
        stream.Position = 0;
        var record = JsonSerializer.Deserialize<GeneratedOwnershipRecord>(stream, JsonOptions);
        if (record is null || record.Version != 2 || record.Owner != owner || record.FileName != Path.GetFileName(path)
            || record.Output is null || string.IsNullOrWhiteSpace(record.Output.Path) || !record.Output.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
            || record.Sha256 is null || record.Sha256.Length != 64 || !record.Sha256.All(Uri.IsHexDigit)
            || record.Inputs is null || record.Inputs.Count is < 1 or > 14 || record.Inputs.Any(x => x is null || string.IsNullOrWhiteSpace(x.Path)))
            throw new IOException("A current producer ownership receipt is required.");
        return record;
    }
    private static string Hash(FileStream stream, int limit)
    {
        if (stream.Length > limit) throw new IOException("The generated file exceeds the ownership-read budget.");
        stream.Position = 0; string hash = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0; return hash;
    }
    private static bool Present(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or JsonException or NotSupportedException;

    private static unsafe void CheckSingleStream(SafeFileHandle handle)
    {
        GeneratedReadLease.CheckLocal(handle, directory: false);
        byte* standard = stackalloc byte[24]; byte* basic = stackalloc byte[40]; byte* streams = stackalloc byte[1024];
        Read(handle, 1, standard, 24); Read(handle, 0, basic, 40); Read(handle, 7, streams, 1024);
        if (LayoutParser.U32(new(standard, 24), 16) != 1 || standard[20] != 0
            || ((FileAttributes)LayoutParser.U32(new(basic, 40), 32) & FileAttributes.ReadOnly) != 0
            || LayoutParser.U32(new(streams, 1024), 0) != 0 || LayoutParser.U32(new(streams, 1024), 4) != 14
            || !new ReadOnlySpan<char>(streams + 24, 7).SequenceEqual("::$DATA")) throw new IOException("Shared, protected or additional-stream content is kept.");
    }
    private static unsafe void Read(SafeFileHandle handle, int kind, byte* buffer, uint size)
    { if (!NativeMethods.GetFileInformationByHandleEx(handle, kind, buffer, size)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }

    private sealed class DeleteLease : IDisposable
    {
        private readonly List<SafeFileHandle> _parents = [];
        public FileStream Stream { get; private set; } = null!;
        public GeneratedInputRevision Revision { get; private set; } = null!;
        public static DeleteLease Open(string path)
        {
            var lease = new DeleteLease();
            try
            {
                var parents = new Stack<string>();
                for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) parents.Push(parent);
                foreach (string parent in parents)
                {
                    using var metadata = GeneratedReadLease.OpenHandle(parent, 0x80, 7);
                    GeneratedReadLease.CheckLocal(metadata, directory: true); var before = GeneratedReadLease.Inspect(metadata, parent);
                    var pinned = GeneratedReadLease.OpenHandle(parent, 0x81, 3); lease._parents.Add(pinned);
                    GeneratedReadLease.CheckLocal(pinned, directory: true); var after = GeneratedReadLease.Inspect(pinned, parent);
                    string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
                    if (before.Volume != after.Volume || before.Identity != after.Identity || name.StartsWith('$')
                        || name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Protected or replaced ancestor storage is kept.");
                }
                var handle = GeneratedReadLease.OpenHandle(path, 0x80010180, 0);
                try
                {
                    CheckSingleStream(handle); lease.Revision = GeneratedReadLease.Inspect(handle, path);
                    lease.Stream = new FileStream(handle, FileAccess.Read, 65536, false);
                }
                catch { handle.Dispose(); throw; }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public unsafe void Delete()
        { byte remove = 1; if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, &remove, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        public void Dispose() { Stream?.Dispose(); foreach (var parent in _parents.AsEnumerable().Reverse()) parent.Dispose(); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte* buffer, uint length);
}

public sealed class GeneratedOwnedReadLease : IDisposable
{
    private readonly IReadOnlyList<GeneratedReadLease> _leases;
    public FileStream Stream { get; }
    internal GeneratedOwnedReadLease(FileStream stream, IReadOnlyList<GeneratedReadLease> leases) { Stream = stream; _leases = leases; }
    public void Verify() { foreach (var lease in _leases) lease.Verify(); }
    public void Dispose() { foreach (var lease in _leases.Reverse()) lease.Dispose(); }
}
