using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Deduplication;

public sealed record DuplicateSearchPolicy
{
    public long MinimumBytes { get; init; } = 64 * 1024;
    public long MaximumReadBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumFiles { get; init; } = 20_000;
    public int MaximumRetainedFiles { get; init; } = 50_000;
    public TimeSpan MaximumTime { get; init; } = TimeSpan.FromSeconds(30);
    public string[] ExcludedPaths { get; init; } = [];
    public int StartRecord { get; init; }
    internal void Validate()
    {
        if (MinimumBytes < 1 || MaximumReadBytes is < 1 or > 64L * 1024 * 1024 * 1024 || MaximumFiles is < 1 or > 250_000
            || MaximumRetainedFiles is < 1 or > 250_000 || MaximumTime <= TimeSpan.Zero || MaximumTime > TimeSpan.FromMinutes(10)
            || StartRecord < 0 || ExcludedPaths is null || ExcludedPaths.Length > 256)
            throw new ArgumentException("Invalid duplicate search limits.");
        foreach (string path in ExcludedPaths) OptimizationPolicy.NormalizePath(path);
    }
}
public sealed record DuplicateFile(string Path, StorageFileRevision Revision, string Hash)
{
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
    public bool HasAdditionalData => Revision.HasAlternateStreams;
    public bool SharesFileObject => Revision.Links > 1;
    public OptimizationEvidence Evidence => new(OptimizationAction.DiscoverDuplicates, OptimizationDisposition.Eligible,
        "filesystem", Path, "Default-stream digest and native revision observed; mutations require current byte verification.",
        RestorationRequirement.ContentsPreserved, ObservedAtUtc) { CoverageComplete = true, Producer = "SHA-256 / native file identity" };
}
public sealed record DuplicateGroup(string Hash, long FileBytes, IReadOnlyList<DuplicateFile> Files)
{
    public long RedundantLogicalBytes => checked(FileBytes * (Files.Count - 1));
    public bool DefaultStreamsOnly => Files.Any(f => f.HasAdditionalData);
    public string Comparison => DefaultStreamsOnly ? "Equal default streams · Additional data retained" : "Byte-verified equal default streams";
    // Independent IDs can already share ReFS extents. This is deliberately not guaranteed physical reclaim.
    public long ObservedAllocatedBytes => Files.Sum(f => f.Revision.AllocatedBytes);
}
public sealed record DuplicateSearchProgress(string Phase, int ExaminedFiles, long ReadBytes);
public sealed record DuplicateSearchResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<DuplicateSearchIssue> Issues,
    int ExaminedFiles, long ReadBytes, int ExistingAliases, bool Complete, bool Cancelled, int NextRecord, string Note);
public sealed record DuplicateSearchIssue(string Path, string Reason);

// Discovery never deletes, links or replaces names. A mutation must reacquire
// and verify both captured identities and full bytes under stable handles.
public static class DuplicateFinder
{
    public static async Task<DuplicateSearchResult> FindAsync(ScanSnapshot scan, DuplicateSearchPolicy? policy = null,
        IProgress<DuplicateSearchProgress>? progress = null, CancellationToken token = default)
    {
        using var session = new DuplicateSearchSession(scan, policy);
        return await session.ContinueAsync(progress, token).ConfigureAwait(false);
    }
    internal static string Hash(StorageFile file, ReadBudget budget)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[64 * 1024]; long offset = 0;
        while (offset < file.Revision.Length)
        {
            budget.Check(); int count = (int)Math.Min(buffer.Length, file.Revision.Length - offset); budget.Consume(count);
            int read = RandomAccess.Read(file.Handle, buffer.AsSpan(0, count), offset);
            if (read != count) throw new IOException("The file ended before its captured length.");
            hash.AppendData(buffer, 0, read); offset += read;
        }
        file.Verify(file.Revision); return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static bool Equal(StorageFile first, StorageFile second, ReadBudget budget)
    {
        if (first.Revision.Length != second.Revision.Length) return false;
        byte[] left = new byte[64 * 1024], right = new byte[left.Length]; long offset = 0;
        while (offset < first.Revision.Length)
        {
            budget.Check(); int count = (int)Math.Min(left.Length, first.Revision.Length - offset); budget.Consume(count * 2L);
            if (RandomAccess.Read(first.Handle, left.AsSpan(0, count), offset) != count
                || RandomAccess.Read(second.Handle, right.AsSpan(0, count), offset) != count) throw new IOException("A comparison ended before its captured length.");
            if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
            offset += count;
        }
        first.Verify(first.Revision); second.Verify(second.Revision); return true;
    }
    internal static bool Expected(Exception error) => error is not BudgetReachedException
        && (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException);

    internal sealed class ReadBudget(long maximum, TimeSpan duration, CancellationToken token)
    {
        private readonly Stopwatch _time = Stopwatch.StartNew();
        public long Bytes { get; private set; }
        internal long Remaining => maximum - Bytes;
        internal TimeSpan Elapsed => _time.Elapsed;
        internal void Check() { token.ThrowIfCancellationRequested(); if (_time.Elapsed >= duration) throw new BudgetReachedException(); }
        internal void Consume(long count) { Check(); if (count > maximum - Bytes) throw new BudgetReachedException(); Bytes += count; }
    }
    internal sealed class BudgetReachedException() : IOException("The content-read or verification-time budget was reached. Continue with a new page or raise the read limit.") { }
}
