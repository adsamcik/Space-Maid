# Inventory evidence for integrations

The host now offers an optional API v1 capability, `IInventoryEvidence`, on `IExtensionHost.Inventory`. It provides deterministic metadata summaries, scoped browsing and strict admission of untrusted investigation proposals. Existing Photos, Apps and Opportunities entrypoints continue to work. There is no model runtime, assistant UI or new cleanup executor in this foundation.

## Scope and trust

A trusted view opens a session after the user chooses a scanned folder and exclusions. The factory accepts paths from that view; **never expose the factory as a model tool**. The session reads only the existing scan snapshot. It does not enumerate the live disk, open contents, hydrate placeholders, launch processes, run SQL or access the network.

```csharp
if (host.Inventory is IInventoryEvidence evidence)
{
    using var session = await evidence.OpenEvidenceSessionAsync(userChosenRoot, userChosenExclusions, token);
    var page = await session.ChildrenAsync(session.Scope.Root.Id, 100, null, token);
    var summary = await session.SummarizeAsync([session.Scope.Root.Id], token);
    // Send bounded records to a future model transport; keep session creation
    // and disposal in the trusted view. Render summary.Text from these facts.
}
```

Browsing, filename search, details and summaries accept only opaque IDs issued in that session. Continuations are also host-issued and bound to the original folder and query. Exclusions use exact scanned name links, including case-sensitive names. Another session's IDs, arbitrary paths, unknown continuations and changed queries are rejected. Linked/offline/recall-on-access leaves remain visible as blocked metadata; their descendants are not followed. A root with unavailable ancestors is rejected.

Scan replacement, a known out-of-date scan, host shutdown or disposal prevents further publication. `Scope.Revision` identifies this evidence session; scan/recheck timestamps and retained-metadata flags identify the underlying observations. Native IDs are scoped to that snapshot, not durable identities across volumes or deletion/recreation. A scan is not an atomic view of the disk. Unobserved changes still require live revalidation before any future action.

This capability is **not an OS sandbox for a DLL**. Trusted in-process extensions retain the user's filesystem permissions. A future local model must run behind a fixed transport that exposes only the session operations, with separate process supervision and an explicitly designed OS boundary if filesystem confinement is needed. Do not give it arbitrary tools, shell access, file paths as authorization or a cleanup service.

## Deterministic summaries

`EvidenceSummary` carries `host.metadata-summary` version 1, selected roots, observation coverage, counts, byte totals, filename type hints, modification-date coverage, cautions and human text derived from those same totals. Serialize the record with `System.Text.Json`; do not ask a model to generate authoritative totals or safety labels.

Containing selections collapse into one union. File allocation/logical sizes count each known physical object once, even when its primary name is outside the selected folder. Name counts still include its aliases. Directory totals are not added to file totals. `SharedObjectLogicalBytes` discloses observed shared objects; incomplete volume-link coverage also remains visible. These are observed regular-file footprints, not guaranteed recoverable bytes, and do not include directory index/storage overhead.

Without physical identity/allocation data, `UniquePhysicalFiles`, `AllocatedBytes` and shared-object size are null. Logical bytes then count observed names and may count physical aliases twice. Named-stream omissions, scan warnings, unmeasured folders, unavailable leaves and retained metadata remain explicit. `TraversalComplete` describes whether the query finished; `Coverage.Complete` also requires complete observed metadata. User exclusions intentionally remove their subtrees from the defined scope.

File types are filename hints from the existing deterministic classifier. Dates are observed modification dates, not last-use evidence. Neither establishes that data is disposable. The host's `host.metadata-review` version 1 policy reports **ManualInvestigation** or **Blocked**. This foundation establishes no general rule that makes arbitrary files eligible for cleanup. Existing deterministic Developer findings and their live cleanup safeguards remain owned by the Opportunities workflow.

## Limits and references

Queries run off the WPF dispatcher and serialize within a session. Limits are 100 items per page/proposal/summary selection, 4,096 issued IDs, eight unfinished page sequences, 128 requests, 20,000 examined entries per query and 250,000 per session. An ancestry chain beyond 512 hops fails explicitly instead of silently omitting a deep subtree. Search returns a continuation even when a bounded page finds no matches. A summary that reaches its work budget returns partial counts and a visible caution; it never presents partial totals as complete. Choose a smaller user-selected scope when a session reaches a limit. Cancellation and disposal close retained iterators.

The older streaming `IInventoryReader` now keeps separate state for up to 16 simultaneous enumerations. Each holds at most 512 location handles. References remain valid only while processing their current batch; the next `MoveNextAsync`, cancellation or disposal expires that reader's batch. A different reader cannot invalidate it. Cached references also expire on scan replacement. Resolve and persist a location/catalog identity while processing its batch; do not retain transient handles across batches.

## Untrusted proposals

The sole admitted action is `investigate`. Use an entry's `Id.Value` as the proposal GUID:

```json
{
  "schemaVersion": 1,
  "items": [
    { "id": "HOST-ISSUED-GUID", "action": "investigate", "explanation": "Model-generated suggestion" }
  ]
}
```

`ValidateProposalAsync` accepts at most 64 KiB of UTF-8 JSON and depth eight. It rejects the entire proposal for malformed/missing/duplicate/unknown fields, duplicate/unknown/out-of-scope IDs, unsupported actions, blocked items or incomplete summary coverage. Extra path, size, confidence, safety or command claims are rejected. It resolves paths, policy and sizes itself; model prose survives only as `UntrustedExplanation`, separate from host facts. Treat filenames and that prose as untrusted display text, not instructions.

Admission returns investigation records and their recalculated summary. It never creates `Opportunity`, `CleanupPlan`, `FolderDeletionTarget` or execution authority. An accepted investigation still needs human review and separately implemented deterministic rules/live checks before any later cleanup. Human approval must not bypass those checks. Cancellation, exhausted budgets and expired sessions report exceptions; callers should discard unfinished results and ask the user to refresh or choose a smaller scope.

Focused checks cover concurrent fresh/cached readers, reference expiration, scan replacement/cancellation, scoped unions with external primary hard links, unknown accounting, exclusions, continuations, cross-session IDs, blocked leaves, partial summaries and strict proposal parsing. A locked-content fixture verifies the capability uses only snapshot metadata. Independent review found a deep-folder omission at the ancestry guard; it now fails explicitly, with a regression fixture.

Verification used committed baseline `2392dba` plus only these task changes in an isolated source copy, preserving concurrent unfinished cleanup/review edits. The full Windows suite passed 257 checks with two existing administrator-only skips; the final affected suite passed 50 checks after the cancellation and review corrections. The Release host/test build succeeded. These establish metadata/API behavior, not a running model adapter, OS confinement or general safety of deleting user data.
