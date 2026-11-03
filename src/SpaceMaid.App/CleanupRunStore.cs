using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

// The store is shared by the visible app and its scheduled entrypoint. Its
// short metadata lock is separate from the long mutation lease, so Pause/Stop
// remain available while a cleanup is running.
internal sealed class CleanupRunStore
{
    private const int MaximumDefinitionsBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new AutomaticCleanupRuleConverter() } };
    private readonly DiskAccess _files;
    private readonly string _directory, _definitionsPath, _runsDirectory, _stopDirectory, _mutexName, _indexPath, _indexDirtyPath;
    internal string DirectoryPath => _directory;

    internal CleanupRunStore(DiskAccess files, string directory)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _directory = Path.GetFullPath(directory);
        _definitionsPath = Path.Join(_directory, "definitions.json");
        _runsDirectory = Path.Join(_directory, "Runs");
        _stopDirectory = Path.Join(_directory, "Stop");
        _indexPath = Path.Join(_directory, "run-index.json");
        _indexDirtyPath = Path.Join(_directory, "run-index.dirty");
        _mutexName = @"Global\SpaceMaid.CleanupStore." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory.ToUpperInvariant())))[..24];
    }

    internal IReadOnlyList<CleanupDefinition> LoadDefinitions() => Locked(LoadDefinitionsCore);

    internal void SaveDefinition(CleanupDefinition definition) => Locked(() =>
    {
        definition = definition.Validate();
        var definitions = LoadDefinitionsCore().ToList();
        int index = definitions.FindIndex(item => item.Id == definition.Id);
        if (index >= 0)
        {
            if (definition.Revision < definitions[index].Revision) throw new InvalidOperationException("A newer cleanup definition is already saved. Reload it before saving.");
            definitions[index] = definition;
        }
        else definitions.Add(definition);
        if (definitions.Count > 256) throw new InvalidOperationException("The saved cleanup limit was reached. Remove an unused cleanup first.");
        Write(_definitionsPath, new DefinitionFile(1, definitions.ToArray()), MaximumDefinitionsBytes);
        return true;
    });

    internal void DeleteDefinition(Guid id) => Locked(() =>
    {
        RequireId(id);
        Write(_definitionsPath, new DefinitionFile(1, LoadDefinitionsCore().Where(item => item.Id != id).ToArray()), MaximumDefinitionsBytes);
        return true;
    });

    internal IReadOnlyList<CleanupRunRecord> LoadRuns() => Locked(LoadRunsCore);
    internal IReadOnlyList<CleanupRunHeader> LoadRunHeaders() => Locked(LoadRunHeadersCore);
    internal CleanupRunRecord? LoadRun(Guid id) => Locked(() => LoadRunCore(id));

    internal void SaveRun(CleanupRunRecord record) => Locked(() =>
    {
        SaveRunCore(record);
        return true;
    });

    internal void AppendReceipt(Guid id, CleanupTargetReceipt receipt) => Locked(() =>
    {
        RequireId(id);
        if (!_files.FileExists(Path.Join(_runsDirectory, id.ToString("N") + ".json")))
            throw new InvalidOperationException("Save the captured cleanup manifest before recording an attempt.");
        _files.AppendAllTextDurable(Path.Join(_runsDirectory, id.ToString("N") + ".journal"), JsonSerializer.Serialize(receipt, Json) + "\n");
        return true;
    });

    internal bool TryQueueRun(CleanupRunRecord record) => Locked(() =>
    {
        if (record.State != CleanupRunState.Queued || record.ApprovalClaimId is null)
            throw new ArgumentException("A unique approval claim is required to queue cleanup.");
        var existing = LoadRunCore(record.Id);
        if (existing is not null && existing.State is not (CleanupRunState.Review or CleanupRunState.Queued)) return false;
        if (existing?.State == CleanupRunState.Queued && (existing.ApprovalClaimId is not null
            || existing.OwnerProcessId != record.OwnerProcessId || existing.OwnerStartedUtc != record.OwnerStartedUtc)) return false;
        RecoverDeadOwnersCore();
        if (LoadRunHeadersCore().Count(item => item.Id != record.Id && item.State == CleanupRunState.Queued) >= 32) return false;
        SaveRunCore(record);
        return true;
    });

    internal bool TryTransitionReview(Guid id, CleanupRunState target, string? message = null) => Locked(() =>
    {
        if (target is not (CleanupRunState.Dismissed or CleanupRunState.Superseded))
            throw new ArgumentException("Choose a terminal pending-review action.", nameof(target));
        var existing = LoadRunCore(id);
        if (existing?.State != CleanupRunState.Review) return false;
        SaveRunCore(existing with { State = target, Message = message, UpdatedUtc = DateTimeOffset.UtcNow });
        return true;
    });

    internal bool HasOccurrence(Guid definitionId, int revision, string occurrenceId) => Locked(() =>
        LoadRunHeadersCore().Any(item => item.DefinitionId == definitionId && item.DefinitionRevision == revision && item.OccurrenceId == occurrenceId));

    internal bool TrySaveOccurrence(CleanupRunRecord record) => Locked(() =>
    {
        if (record.OccurrenceId is null) throw new ArgumentException("A scheduled occurrence ID is required.");
        if (LoadRunHeadersCore().Any(item => item.DefinitionId == record.Definition.Id && item.DefinitionRevision == record.Definition.Revision
            && item.OccurrenceId == record.OccurrenceId)) return false;
        if (record.State == CleanupRunState.Queued)
        {
            RecoverDeadOwnersCore();
            if (LoadRunHeadersCore().Count(item => item.State == CleanupRunState.Queued) >= 32)
                throw new InvalidOperationException("The cleanup queue is full. This scheduled occurrence was not started.");
        }
        SaveRunCore(record);
        return true;
    });

    internal bool RequestStop(Guid id) => Locked(() =>
    {
        var run = LoadRunCore(id);
        if (run is null || run.State is not (CleanupRunState.Running or CleanupRunState.Queued)) return false;
        Write(Path.Join(_stopDirectory, id.ToString("N") + ".json"), new StopRequest(id));
        return true;
    });

    internal bool IsStopRequested(Guid id)
    {
        RequireId(id);
        return _files.FileExists(Path.Join(_stopDirectory, id.ToString("N") + ".json"));
    }

    internal void ClearStop(Guid id) => Locked(() =>
    {
        RequireId(id);
        string path = Path.Join(_stopDirectory, id.ToString("N") + ".json");
        if (_files.FileExists(path)) _files.DeleteFile(path);
        return true;
    });

    // Recovery requires exact process-start identity proof, not a timeout. A
    // live owner can still be preparing a queued occurrence without the lease.
    internal void RecoverInterruptedRuns() => Locked(() =>
    {
        RecoverDeadOwnersCore();
        return true;
    });

    private void RecoverDeadOwnersCore()
    {
        foreach (var run in LoadRunHeadersCore().Where(item => item.State is CleanupRunState.Running or CleanupRunState.Queued)
            .Select(item => LoadRunCore(item.Id)!).Where(item => item is not null)
            .Where(CleanupRunOwnership.IsDefinitelyGone))
        {
            var receipts = run.Receipts.Select(item => item.Attempted && item.Outcome == CleanupOutcome.Uncertain
                ? item with { Message = "The previous process ended before confirming this outcome. Inspect it before retrying." } : item).ToArray();
            SaveRunCore(run with { State = run.HasUncertainOutcome
                ? CleanupRunState.NeedsAttention : CleanupRunState.Stopped, Receipts = receipts,
                UpdatedUtc = DateTimeOffset.UtcNow, Message = "Interrupted. No cleanup resumed automatically." });
        }
    }

    private IReadOnlyList<CleanupDefinition> LoadDefinitionsCore()
    {
        if (!_files.FileExists(_definitionsPath)) return [];
        var document = Read<DefinitionFile>(_definitionsPath, MaximumDefinitionsBytes);
        if (document.Version != 1 || document.Definitions is null || document.Definitions.Length > 256)
            throw new InvalidDataException("Saved cleanup definitions have an unsupported format.");
        var definitions = document.Definitions.Select(item => item.Validate()).ToArray();
        if (definitions.Select(item => item.Id).Distinct().Count() != definitions.Length)
            throw new InvalidDataException("Saved cleanup IDs are not unique.");
        return definitions;
    }

    private IReadOnlyList<CleanupRunRecord> LoadRunsCore()
    {
        if (!_files.DirectoryExists(_runsDirectory)) return [];
        var records = new List<CleanupRunRecord>();
        foreach (string path in _files.EnumerateFiles(_runsDirectory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id)) continue;
            var record = LoadRunCore(id);
            if (record is not null) records.Add(record);
        }
        return records.OrderByDescending(item => item.CreatedUtc).ToArray();
    }

    private IReadOnlyList<CleanupRunHeader> LoadRunHeadersCore()
    {
        if (_files.FileExists(_indexDirtyPath) || !_files.FileExists(_indexPath))
            return RebuildRunHeadersCore();
        RunIndex index;
        try { index = Read<RunIndex>(_indexPath, 64 * 1024 * 1024); }
        catch (InvalidDataException) { return RebuildRunHeadersCore(); }
        if (index.Version != 1 || index.Runs is null || index.Runs.Any(item => item is null || item.Id == Guid.Empty || !Enum.IsDefined(item.State))
            || index.Runs.Select(item => item.Id).Distinct().Count() != index.Runs.Length)
            return RebuildRunHeadersCore();
        return index.Runs.OrderByDescending(item => item.CreatedUtc).ToArray();
    }

    private IReadOnlyList<CleanupRunHeader> RebuildRunHeadersCore()
    {
        if (!_files.DirectoryExists(_runsDirectory)) return [];
        var rebuilt = LoadRunsCore().Select(Header).ToArray();
        Write(_indexPath, new RunIndex(1, rebuilt));
        if (_files.FileExists(_indexDirtyPath)) _files.DeleteFile(_indexDirtyPath);
        return rebuilt.OrderByDescending(item => item.CreatedUtc).ToArray();
    }

    private CleanupRunRecord? LoadRunCore(Guid id)
    {
        RequireId(id);
        string path = Path.Join(_runsDirectory, id.ToString("N") + ".json");
        if (!_files.FileExists(path)) return null;
        var record = Read<CleanupRunRecord>(path, 64 * 1024 * 1024);
        ValidateRecord(record);
        string journalPath = Path.Join(_runsDirectory, id.ToString("N") + ".journal");
        if (_files.FileExists(journalPath))
        {
            using var input = _files.OpenRead(journalPath);
            if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("Cleanup attempt journal is too large.");
            using var reader = new StreamReader(input, Encoding.UTF8);
            var receipts = record.Receipts.ToDictionary(Key);
            while (reader.ReadLine() is { } line)
            {
                // An abruptly terminated append leaves the preceding durable
                // starting entry authoritative. Never invent a terminal result.
                try
                {
                    var receipt = JsonSerializer.Deserialize<CleanupTargetReceipt>(line, Json)
                        ?? throw new InvalidDataException("Cleanup attempt journal contains an empty record.");
                    receipts[Key(receipt)] = receipt;
                }
                catch (JsonException) when (reader.EndOfStream) { break; }
                catch (JsonException error) { throw new InvalidDataException("Cleanup attempt journal is damaged. Keep it for recovery.", error); }
            }
            record = record with { Receipts = receipts.Values.ToArray() };
        }
        ValidateRecord(record);
        if (record.Id != id) throw new InvalidDataException("Cleanup receipt ID does not match its file.");
        return record;
    }

    private void SaveRunCore(CleanupRunRecord record)
    {
        ValidateRecord(record);
        var existing = LoadRunCore(record.Id);
        if (existing is not null) ValidateTransition(existing, record);
        var index = LoadRunHeadersCore().Where(item => item.Id != record.Id).ToList();
        Write(_indexDirtyPath, new { record.Id });
        Write(Path.Join(_runsDirectory, record.Id.ToString("N") + ".json"), record);
        string journalPath = Path.Join(_runsDirectory, record.Id.ToString("N") + ".journal");
        if (_files.FileExists(journalPath)) _files.DeleteFile(journalPath);
        index.Add(Header(record));
        Write(_indexPath, new RunIndex(1, index.ToArray()));
        _files.DeleteFile(_indexDirtyPath);
    }

    private static void ValidateTransition(CleanupRunRecord existing, CleanupRunRecord next)
    {
        bool active = existing.State is CleanupRunState.Queued or CleanupRunState.Running;
        bool claimed = existing.ApprovalClaimId is not null;
        bool sameOwner = existing.OwnerProcessId == next.OwnerProcessId && existing.OwnerStartedUtc == next.OwnerStartedUtc;
        bool sameClaim = existing.ApprovalClaimId == next.ApprovalClaimId;
        if (active && claimed && (!sameClaim || !sameOwner))
            throw new InvalidOperationException("This cleanup run is owned by another approval. Reload its current state.");
        if (existing.State == CleanupRunState.Running && next.State is CleanupRunState.Review or CleanupRunState.Queued or CleanupRunState.Dismissed or CleanupRunState.Superseded)
            throw new InvalidOperationException("A running cleanup cannot be overwritten by an older review action.");
        if (existing.State == CleanupRunState.Queued && next.State is CleanupRunState.Dismissed or CleanupRunState.Superseded)
            throw new InvalidOperationException("This cleanup was already approved. Stop the queued run instead.");
        if (existing.State is CleanupRunState.Completed or CleanupRunState.Stopped or CleanupRunState.NeedsAttention or CleanupRunState.Missed or CleanupRunState.Dismissed or CleanupRunState.Superseded)
        {
            if (JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(next, Json))
                throw new InvalidOperationException("A finished cleanup receipt cannot be overwritten. Start a new review instead.");
            return;
        }
        bool reservingManifest = existing.State == CleanupRunState.Queued && !claimed && existing.Targets.Length == 0 && sameOwner;
        if (!reservingManifest && JsonSerializer.Serialize(existing.Definition, Json) != JsonSerializer.Serialize(next.Definition, Json))
            throw new InvalidOperationException("The captured cleanup definition cannot change inside a saved run.");
        if (!reservingManifest)
        {
            var original = existing.Targets.ToDictionary(item => (item.Location.ProviderId, item.Location.RuleId, item.Location.Path));
            if (original.Count != next.Targets.Length) throw new InvalidOperationException("A saved cleanup manifest cannot add or remove captured folders.");
            foreach (var target in next.Targets)
            {
                if (!original.TryGetValue((target.Location.ProviderId, target.Location.RuleId, target.Location.Path), out var captured)
                    || target.Identity != captured.Identity || JsonSerializer.Serialize(target.Location, Json) != JsonSerializer.Serialize(captured.Location, Json)
                    || target.Decision != captured.Decision && !(existing.State == CleanupRunState.Review && captured.Decision == "Ready" && target.Decision == "Skipped"))
                    throw new InvalidOperationException("A saved cleanup manifest cannot substitute a captured folder or widen its approval.");
            }
        }
    }

    private static void ValidateRecord(CleanupRunRecord record)
    {
        RequireId(record.Id);
        if (record.SchemaVersion != 1 || record.Definition is null || record.Targets is null || record.Receipts is null
            || record.Targets.Length > 100000 || record.Receipts.Length > 100000 || !Enum.IsDefined(record.State)
            || record.Source is null || record.Source.Length > 80 || record.Message?.Length > 8192 || record.OccurrenceId?.Length > 256)
            throw new InvalidDataException("Cleanup receipt has an unsupported format.");
        record.Definition.Validate();
        var scopes = record.Definition.ScopePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in record.Targets)
            if (target is null || target.Location is null || !CleanupDefinition.InScopes(target.Location.Path, scopes)
                || target.Decision is not ("Ready" or "Kept" or "Skipped")
                || target.Identity is not null && !string.Equals(target.Identity.Path, target.Location.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A cleanup receipt contains an invalid target.");
        if (record.Receipts.Any(item => item is null)) throw new InvalidDataException("A cleanup receipt contains an empty outcome.");
        var keys = record.Targets.Select(item => (item.Location.Path, item.Location.ProviderId, item.Location.RuleId)).ToHashSet();
        if (keys.Count != record.Targets.Length || record.Receipts.Select(Key).Distinct().Count() != record.Receipts.Length)
            throw new InvalidDataException("A cleanup receipt contains duplicate target records.");
        foreach (var receipt in record.Receipts)
            if (receipt is null || !Enum.IsDefined(receipt.Outcome) || receipt.Message is null || receipt.Message.Length > 8192
                || receipt.RecycledPath?.Length > 4096
                || !keys.Contains(Key(receipt)))
                throw new InvalidDataException("A cleanup receipt refers to an uncaptured target.");
    }

    private T Read<T>(string path, long maximumBytes)
    {
        using var input = _files.OpenRead(path);
        if (input.Length > maximumBytes) throw new InvalidDataException("Cleanup data is too large to read safely.");
        try { return JsonSerializer.Deserialize<T>(input, Json) ?? throw new InvalidDataException("Cleanup data is empty."); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Saved cleanup data is damaged or unsupported. Keep the original file for recovery.", error); }
    }

    private void Write<T>(string path, T value, int maximumBytes = 64 * 1024 * 1024)
    {
        string json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new InvalidOperationException("Cleanup data is too large to save. Use project scopes or remove unused saved cleanups.");
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllTextAtomicDurable(path, json);
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool entered;
        try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new IOException("Cleanup data is busy. Try again shortly.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static void RequireId(Guid id)
    { if (id == Guid.Empty) throw new ArgumentException("A cleanup ID is required."); }
    private sealed record DefinitionFile(int Version, CleanupDefinition[] Definitions);
    private sealed record StopRequest(Guid RunId);
    private sealed record RunIndex(int Version, CleanupRunHeader[] Runs);
    // The public producer contract intentionally accepts IEnumerable<string>
    // while exposing IReadOnlyList<string>. Keep that callable API intact and
    // explicitly reconstruct every proof field when reading durable manifests.
    private sealed class AutomaticCleanupRuleConverter : JsonConverter<AutomaticCleanupRule>
    {
        public override AutomaticCleanupRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var value = document.RootElement;
                return new(value.GetProperty("Id").GetString() ?? throw new JsonException("A producer ID is required."),
                    value.GetProperty("Name").GetString() ?? throw new JsonException("A producer name is required."),
                    value.GetProperty("Regeneration").GetString() ?? throw new JsonException("A rebuild description is required."),
                    value.GetProperty("FileSuffixes").EnumerateArray().Select(item => item.GetString()
                        ?? throw new JsonException("A generated file suffix is required.")).ToArray(),
                    value.GetProperty("MinimumAgeDays").GetInt32(), value.GetProperty("RequiresPythonSource").GetBoolean(),
                    (GeneratedResourceKind)value.GetProperty("ResourceKind").GetInt32());
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { throw new JsonException("The saved generated-file producer contract is invalid.", error); }
        }

        public override void Write(Utf8JsonWriter writer, AutomaticCleanupRule value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new AutomaticRuleData(value.Id, value.Name, value.Regeneration, value.FileSuffixes.ToArray(),
                value.MinimumAgeDays, value.RequiresPythonSource, value.ResourceKind), options);
    }
    private sealed record AutomaticRuleData(string Id, string Name, string Regeneration, string[] FileSuffixes,
        int MinimumAgeDays, bool RequiresPythonSource, GeneratedResourceKind ResourceKind);
    private static CleanupRunHeader Header(CleanupRunRecord record) => new(record.Id, record.Definition.Id, record.Definition.Revision,
        record.Definition.Name, record.CreatedUtc, record.UpdatedUtc, record.State, record.Source, record.Message, record.OccurrenceId,
        record.HasUncertainOutcome || record.State == CleanupRunState.Running, record.Targets.Length,
        record.State == CleanupRunState.Review ? record.Targets.Count(item => item.Decision == "Ready") : 0,
        record.Targets.Count(item => item.Decision == "Kept"), record.Receipts.Count(item => item.Outcome == CleanupOutcome.Recycled),
        record.Receipts.Count(item => item.Outcome == CleanupOutcome.PermanentlyDeleted), record.Receipts.Count(item => item.Outcome == CleanupOutcome.Skipped),
        record.Receipts.Count(item => item.Outcome == CleanupOutcome.NotProcessed),
        record.Receipts.Count(item => item.Outcome is CleanupOutcome.Partial or CleanupOutcome.Uncertain
            || item.Attempted && item.Outcome is CleanupOutcome.Failed or CleanupOutcome.Cancelled));
    private static (string Path, string Provider, string Rule) Key(CleanupTargetReceipt receipt)
        => (receipt.Path, receipt.ProviderId, receipt.RuleId);
}
