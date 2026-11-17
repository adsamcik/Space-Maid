using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Core.Scanning;

public sealed record CloudFileState(string Provider, bool IsDirectory, long LocalBytes, long ModifiedBytes, bool InSync, int PinState)
{
    internal FileIdentity Identity { get; init; }
    internal ulong Volume { get; init; }
    internal long SyncRootId { get; init; }
    public bool CanRequestOnlineOnly => !IsDirectory && InSync && ModifiedBytes == 0 && PinState != 3 && LocalBytes > 0;
}

public enum CloudInspectionStatus { Supported, Unsupported, Unavailable }

// Unavailable is unknown capability, never evidence that a file is local-only
// or eligible for mutation. Keep the diagnostic separate from the live state.
public sealed record CloudFileInspection(CloudInspectionStatus Status, CloudFileState? State, string? Error);

public sealed class OnlineOnlyPlan
{
    internal sealed record Target(string Path, CloudFileState State);
    internal IReadOnlyList<Target> Targets { get; }
    public string Path { get; }
    public string Provider { get; }
    public int EligibleCount => Targets.Count;
    public int SkippedCount { get; }
    public long LocalBytes => Targets.Sum(target => target.State.LocalBytes);
    public IReadOnlyList<string> Warnings { get; }
    internal OnlineOnlyPlan(string path, string provider, IReadOnlyList<Target> targets, int skipped, IReadOnlyList<string> warnings)
    { Path = path; Provider = provider; Targets = targets; SkippedCount = skipped; Warnings = warnings; }
}

public sealed record OnlineOnlyProgress(int Processed, int Total, int Requested, int Skipped);
public sealed record OnlineOnlyResult(int Requested, int ConfirmedOnlineOnly, int Skipped, bool Cancelled, IReadOnlyList<string> Warnings);

// Capability detection, never a folder-name/provider-name guess. No content
// reads, deletion, forced dehydration, or changes to the provider's sync state.
public static partial class CloudFiles
{
    public static CloudFileState? Inspect(string path)
        => InspectDetailed(path).State;

    public static CloudFileInspection InspectDetailed(string path)
    {
        try { using var handle = Open(path); return InspectHandleDetailed(handle); }
        catch (Exception ex) when (InspectionFailure(ex))
        { return new(CloudInspectionStatus.Unavailable, null, ex.Message); }
    }

    private static CloudFileInspection InspectHandleDetailed(SafeFileHandle handle)
    {
        try
        {
            var state = Inspect(handle);
            return new(state is null ? CloudInspectionStatus.Unsupported : CloudInspectionStatus.Supported, state, null);
        }
        catch (Exception ex) when (InspectionFailure(ex))
        { return new(CloudInspectionStatus.Unavailable, null, ex.Message); }
    }

    private static bool InspectionFailure(Exception error) => error is IOException or Win32Exception
        or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException;

    private static CloudFileState RequireSupportedInspection(string path)
    {
        var inspection = InspectDetailed(path);
        if (inspection.Status == CloudInspectionStatus.Unavailable)
            throw new IOException("Cloud availability could not be inspected. Retry when the location and provider are available. " + inspection.Error);
        return inspection.State ?? throw new IOException("This location has no supported Windows online-only capability.");
    }

    public static OnlineOnlyPlan Prepare(ScanSnapshot scan, int link, CancellationToken token)
    {
        string path = scan.EntryPath(link) ?? throw new IOException("An unresolved location cannot be made online-only.");
        ValidateAncestors(path);
        var selected = RequireSupportedInspection(path);
        if (scan.HasAllocationData && selected.Identity != scan.Records[scan.Entry(link).FileIndex].Identity)
            throw new IOException("This location changed since scanning. Recheck it first.");
        return Prepare(path, selected, token);
    }

    public static OnlineOnlyPlan Prepare(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = NormalizeLocalPath(path);
        ValidateAncestors(path);
        var selected = RequireSupportedInspection(path);
        return Prepare(path, selected, token);
    }

    private static OnlineOnlyPlan Prepare(string path, CloudFileState selected, CancellationToken token)
    {
        var targets = new List<OnlineOnlyPlan.Target>();
        var warnings = new List<string>();
        int skipped = 0;
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out string? current))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (HasUnsupportedLink(current)) { Skip($"{current}: A linked location was skipped."); continue; }
                var inspection = InspectDetailed(current);
                if (inspection.Status == CloudInspectionStatus.Unavailable)
                { Skip($"{current}: Cloud availability unavailable; retry when accessible. {inspection.Error}"); continue; }
                var state = inspection.State;
                if (state is null || state.SyncRootId != selected.SyncRootId || state.Volume != selected.Volume)
                { Skip($"{current}: A local-only or unsupported location was skipped."); continue; }
                if (state.IsDirectory)
                {
                    foreach (string child in DiskAccess.Default.EnumerateFileSystemEntries(current)) { token.ThrowIfCancellationRequested(); pending.Push(child); }
                }
                else if (state.CanRequestOnlineOnly) targets.Add(new(current, state));
                else Skip(current + ": " + (state.LocalBytes == 0 ? "An already online-only file was skipped." : "An unsynced or excluded file was skipped."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { Skip(current + ": " + ex.Message); }
        }
        return new(path, selected.Provider, targets.AsReadOnly(), skipped, warnings.AsReadOnly());

        void Skip(string reason) { skipped++; if (warnings.Count < 8 && !warnings.Contains(reason)) warnings.Add(reason); }
    }

    public static OnlineOnlyResult Execute(OnlineOnlyPlan plan, IProgress<OnlineOnlyProgress>? progress, CancellationToken token)
    {
        int requested = 0, confirmed = 0, skipped = plan.SkippedCount, processed = 0;
        var warnings = new List<string>();
        var timer = Stopwatch.StartNew();
        foreach (var target in plan.Targets)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                ValidateAncestors(target.Path);
                using var handle = Open(target.Path);
                var live = Inspect(handle);
                if (live is null || live.Identity != target.State.Identity || live.Volume != target.State.Volume
                    || live.SyncRootId != target.State.SyncRootId || !live.CanRequestOnlineOnly)
                    throw new IOException("A file changed or is no longer eligible; it was skipped.");
                // UNPINNED is the supported online-only intent. The sync app
                // processes it asynchronously; never claim immediate release.
                Marshal.ThrowExceptionForHR(CfSetPinState(handle, 2, 0, 0));
                requested++;
                var confirmation = InspectHandleDetailed(handle);
                if (confirmation.State is { LocalBytes: 0 }) confirmed++;
                else if (confirmation.Status != CloudInspectionStatus.Supported && warnings.Count < 8)
                    warnings.Add(target.Path + ": Request accepted; current local storage could not be confirmed. Refresh to recheck. " + confirmation.Error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException)
            { skipped++; if (warnings.Count < 8) warnings.Add(target.Path + ": " + ex.Message); }
            processed++;
            if (timer.ElapsedMilliseconds >= 1000)
            { progress?.Report(new(processed, plan.EligibleCount, requested, skipped)); timer.Restart(); }
        }
        progress?.Report(new(processed, plan.EligibleCount, requested, skipped));
        return new(requested, confirmed, skipped, token.IsCancellationRequested, warnings.AsReadOnly());
    }

    internal static bool HasUnsupportedLink(string path)
    {
        using var handle = Open(path);
        var tags = Information(handle, 9, 8);
        uint attributes = LayoutParser.U32(tags, 0), tag = LayoutParser.U32(tags, 4);
        if ((tag & 0x20000000) != 0) return true;
        if ((attributes & (uint)FileAttributes.ReparsePoint) == 0) return false;
        uint cloud = CfGetPlaceholderStateFromAttributeTag(attributes, tag);
        return cloud == uint.MaxValue || (cloud & 3) == 0;
    }

    private static void ValidateAncestors(string path)
    {
        for (string? part = path; part is not null; part = System.IO.Path.GetDirectoryName(part))
            if (HasUnsupportedLink(part)) throw new IOException("Linked paths are not supported for online-only requests.");
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = DiskAccess.Default.OpenHandle(NativeMethods.ExtendedPath(path), 0x80, 3, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }

    private static unsafe byte[] Information(SafeFileHandle handle, int info, int length)
    {
        var data = new byte[length];
        fixed (byte* buffer = data)
            if (!DiskAccess.Default.ReadHandleInformation(handle, info, buffer, (uint)length)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return data;
    }

    private static unsafe CloudFileState? Inspect(SafeFileHandle handle)
    {
        var tags = Information(handle, 9, 8);
        uint attributes = LayoutParser.U32(tags, 0), tag = LayoutParser.U32(tags, 4);
        if ((tag & 0x20000000) != 0) return null;
        var root = new byte[1028];
        fixed (byte* buffer = root)
            if (!InspectionInformationAvailable(CfGetSyncRootInfoByHandle(handle, 2, buffer, (uint)root.Length, out uint _))) return null;
        var file = new byte[4200];
        bool placeholder;
        fixed (byte* buffer = file)
        {
            placeholder = InspectionInformationAvailable(CfGetPlaceholderInfo(handle, 1, buffer, (uint)file.Length, out uint _));
        }
        bool directory = (attributes & (uint)FileAttributes.Directory) != 0;
        // Sync roots, and ordinary directories below them, need not themselves
        // be placeholders. They can be traversed without treating ordinary
        // unsynced files as eligible for an online-only request.
        if (!placeholder && !directory) return null;
        long syncRootId;
        if (placeholder) syncRootId = LayoutParser.I64(file, 48);
        else
        {
            Array.Clear(file);
            var basic = new byte[8];
            fixed (byte* buffer = basic)
                if (!InspectionInformationAvailable(CfGetSyncRootInfoByHandle(handle, 0, buffer, (uint)basic.Length, out uint _))) return null;
            syncRootId = LayoutParser.I64(basic, 0);
        }
        var id = Information(handle, 18, 24);
        string provider = MemoryMarshal.Cast<byte, char>(root.AsSpan(4, 512)).ToString().TrimEnd('\0');
        return new(provider, directory,
            LayoutParser.I64(file, 0), LayoutParser.I64(file, 16), LayoutParser.U32(file, 36) == 1, (int)LayoutParser.U32(file, 32))
        { Identity = new(LayoutParser.U64(id, 8), LayoutParser.U64(id, 16)), Volume = LayoutParser.U64(id, 0), SyncRootId = syncRootId };
    }

    internal static bool InspectionInformationAvailable(int result)
    {
        if (result >= 0) return true;
        // HRESULT_FROM_WIN32: NOT_SUPPORTED, NOT_A_CLOUD_FILE,
        // CLOUD_FILE_NOT_UNDER_SYNC_ROOT, NOT_A_REPARSE_POINT (Windows SDK).
        // Only explicit absence/unsupported results establish no capability;
        // provider, permission and truncated-buffer failures remain unknown.
        if (result is unchecked((int)0x80070032) or unchecked((int)0x80070178)
            or unchecked((int)0x80070186) or unchecked((int)0x80071126)) return false;
        var error = Marshal.GetExceptionForHR(result);
        throw new IOException("Windows could not inspect cloud availability. " + error?.Message, error);
    }

    [LibraryImport("cldapi.dll")]
    private static partial int CfSetPinState(SafeFileHandle handle, int state, uint flags, nint overlapped);
    [LibraryImport("cldapi.dll")]
    private static unsafe partial int CfGetPlaceholderInfo(SafeFileHandle handle, int info, byte* buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll")]
    private static unsafe partial int CfGetSyncRootInfoByHandle(SafeFileHandle handle, int info, byte* buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll")]
    private static partial uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
}
