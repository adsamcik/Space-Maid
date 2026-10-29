using System.Runtime.CompilerServices;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

/// <summary>Shared disk boundary. Callers request an operation, never an NTFS backend.</summary>
public sealed partial class DiskAccess(Func<INtfsLayoutSource?>? ntfsSource = null)
{
    public static DiskAccess Default { get; } = new();
    private static readonly INtfsLayoutSource NativeSource = new NativeNtfsLayoutSource();
    private static readonly ConditionalWeakTable<INtfsLayoutSource, SerializedSource> Sources = new();

    // An explicitly supplied factory can disable fast access with null. Resolve
    // it on each request so helper disconnects/reconnections take effect.
    internal INtfsLayoutSource? MetadataSource()
    {
        var source = ntfsSource is null ? NativeSource : ntfsSource();
        return Serialize(source);
    }

    internal static INtfsLayoutSource? Serialize(INtfsLayoutSource? source) => source is null or SerializedSource
        ? source : Sources.GetValue(source, value => new(value));

    public Task<ScanSnapshot> ScanAsync(string path, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<ScanDiagnostic>? diagnostic = null,
        string? fastUnavailableReason = null)
    {
        var source = MetadataSource();
        return new VolumeScanner(source).ScanAsync(path, progress, cancellationToken,
            preferFast: source is not null, diagnostic: diagnostic, fastUnavailableReason: fastUnavailableReason);
    }

    public Task<ScanSnapshot> MeasureFolderAsync(string path, CancellationToken token = default,
        Action<string, object>? diagnostic = null, ScanSnapshot? previous = null)
        => new VolumeScanner(MetadataSource()).MeasureFolderAsync(path, token, diagnostic, previous);

    public Task<ScanSnapshot> RecheckAsync(ScanSnapshot scan, IEnumerable<ScanRecheckRequest> requests,
        CancellationToken token = default, Action<string, object>? diagnostic = null)
        => ScanRechecker.RefreshAsync(scan, requests, token, diagnostic, MetadataSource());

    public Task<OpportunityReviewValidation> ReviewCleanupAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default, Action<string, object>? diagnostic = null, OpportunityDiscovery? discover = null,
        IProgress<OpportunityValidationProgress>? progress = null)
        => OpportunityReviewValidator.ValidateAsync(scan, selected, token, diagnostic, discover, progress, MetadataSource());

    public Task<BuildCleanupAssessment> CheckBuildActivityAsync(Opportunity location, BuildCleanupPolicy policy,
        CancellationToken token = default, ScanSnapshot? previous = null)
        => BuildCleanupProtection.CheckCurrentAsync(location, policy, token, previous, MetadataSource());

    // Serialize by source identity, across requests and DiskAccess instances.
    // Native/helper callbacks borrow buffers until they return. Directory reads
    // remain parallel; only the shared source's protocol is serialized.
    private sealed class SerializedSource(INtfsLayoutSource source) : INtfsLayoutSource
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
        {
            _gate.Wait(token);
            try { source.Acquire(volume, consume, token); }
            finally { _gate.Release(); }
        }
        public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
        {
            _gate.Wait(token);
            try { source.AcquireFiles(volume, files, consume, token); }
            finally { _gate.Release(); }
        }
        public NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token)
        {
            _gate.Wait(token);
            try { return source.ReadJournal(volume, since, token); }
            finally { _gate.Release(); }
        }
    }
}
