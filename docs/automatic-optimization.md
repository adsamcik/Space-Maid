# Automatic optimization

Scan a location, then choose **Auto optimize**. Select rules, preview exact files and use **Delete previewed files** for one run. Select **Optimize automatically** and **Save settings** for recurring cleanup. All automatic actions are off by default.

Generated-file eviction permanently deletes verified resources; regeneration replaces undo. Ordinary folder cleanup still uses the Recycle Bin. Keep a file or its containing resource with **Keep file** / **Keep folder**, then save the exclusion for automatic runs.

## Producer evidence

Names and age only identify candidates. The host requires a supported producer contract, readable inputs, current native identities and exact content verification before eviction. Unknown formats and incomplete evidence remain protected.

| Resource | Required evidence | Kept |
| --- | --- | --- |
| CPython 3.11–3.14 checked-hash bytecode | Direct recognized `__pycache__` child, matching magic and code marker, readable source and exact checked source hash | Timestamp caches, orphans, unchecked hashes, mismatches, unsupported versions/layouts |
| .NET compiler fingerprints | Known SDK project, exact Debug/Release and target-framework/RID layout, 64-character input fingerprint and exact FileListAbsolute producer entry | Generated-looking source, arbitrary `.cache`, binaries and unknown variants |
| mypy JSON cache | Paired 1.x metadata/data, exact data and source hashes, readable current source and bounded matching dependency records | Metadata records, unfamiliar JSON, unavailable/external dependencies and unsupported formats |
| Space Maid photo previews | Exact producer receipt binding output SHA-256 to readable unchanged originals | Unfamiliar copied PNGs, missing receipts and unavailable/changed originals |

These contracts establish resource ownership and the inputs required by the supported cache format. Space Maid does not execute project code, install build tools or guarantee that a future build succeeds. The next import, build, type check or preview may take longer. Files outside these precise contracts remain available for review or content-preserving storage actions.

Timestamp Python headers contain modification time and source length, which can still match edited source. Native regression fixtures reproduced this case, so automatic eviction requires the checked source hash. The revised Python rule needs fresh consent. Optional native tests compile owned fixtures with existing uv-managed CPython 3.13/3.14 and verify exact checked-hash regeneration; they never execute project modules. Native npm verification is also exercised against an isolated offline cache, preserving source archives and cached payload bytes. These fixtures do not establish supported pnpm pruning or ReFS execution on unavailable tooling/storage.

## Bounds and execution

Defaults retain activity within 14 days, remove at most 500 files / 512 MiB, examine at most 20,000 inventory entries and read at most 32 MiB of proof data. Each metadata/proof phase has a 20-second limit checked between bounded operations. One proof record is limited to 8 MiB and 16 held inputs. Limits are ceilings, not targets. The byte mutation limit uses the larger of logical and observed allocation size.

A saved cursor advances through the inventory even when early candidates cannot be evicted. Sharing violations receive a short revision-specific cooldown. Partial inspection remains visible and can continue. Scope changes, rule changes and explicit exclusions constrain subsequent work; provider consent includes the producer contract version.

The host reopens exact files and required inputs, checks volume/file IDs, revisions, contents, streams, link counts and availability, and holds effective directory-list and data leases through the operation. Metadata-only opens cannot substitute for readable source or directory rename protection. Reparse, recall/offline, shared, read-only, encrypted, replaced and otherwise unsupported files stay. Generated-file eviction never recursively deletes directories.

Output verification suppresses access-time updates for its own native handle using Windows' documented `SetFileTime(-1)` sentinel, without restoring timestamps or suppressing other readers. This requires metadata attribute access; if unavailable, the file is kept. Repeated previews therefore do not make their own candidates appear newly used. [Windows per-handle access-time suppression](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfiletime).

Automatic runs are attempted after scans and hourly while the configured location remains open. A five-minute timer checks whether a run is due. Scans, cleanup, extension jobs and storage dialogs pause automatic work. There is no background service or work while the app is closed. Changing the open scan does not extend the saved scope.

NTFS inventories refresh affected metadata after mutation. Logical-only inventories require a new scan before unattended work resumes. Removed logical contents are reported separately from guaranteed physical reclaim. Cancellation stops subsequent files; completed deletions remain.

Settings, cursor, cooldowns and the last summary live in `optimization-settings.json`. Per-file outcomes live under `Logs/Optimization`, retaining the latest ten logs. Writable settings and an activity log are required before mutation. Invalid settings disable unattended work, and attempt throttling applies to failures too.

## Other optimization actions

**More → Duplicates and storage** provides resumable identity-aware hashing and byte comparison, explicit NTFS compression of cold build resources, and capability-gated ReFS block sharing. Both names and payloads remain. See [duplicates and storage](duplicates-and-storage.md) for supported files, bounds and hardware limitations.

**More → Tool cache maintenance** probes installed npm, pnpm, NuGet, Cargo and Gradle using fixed native commands and local metadata. Supported npm 9–11 verification and pnpm 9–10 pruning require a fresh plan and explicit confirmation. The plan binds the launcher/version and configured cache root, shows the exact command and warns about later download/private-feed costs. Commands run with bounded output/time in a neutral guarded directory; project scripts, package installs and project evaluation are excluded. Unsupported versions, wrappers or roots are protected. NuGet selective eviction and Cargo/Gradle project-cache deletion remain unavailable when the tool cannot prove a safe selective action. Old age and a lockfile alone do not authorize package removal.

**More → Managed worktrees** separately checks upstream retention, an explicitly selected integration reference, patch equivalence, ignored/untracked content and operation/activity evidence. Age alone never retires a checkout. Enrollment requires a named branch and a reproducible linked checkout whose exact HEAD is retained in the configured integration target. Default inactivity is 30 days with seven days of recovery grace; automatic retirement is an additional opt-in.

Retirement creates a durable recovery ref and record, then uses Git's worktree move to quarantine on the same volume. Quarantine still occupies space. Recovery can restore the checkout while it remains unchanged. After grace, finalization rechecks Git state and deletes only captured unchanged objects through native handles, retaining branches and shared Git storage. New/changed files, missing coverage, active operations, ignored data and unsupported move conditions stop retirement. No force or broad prune fallback is used. A bounded metadata preflight precedes inventory creation; each assessment has a 20-second deadline. The content ledger admits at most 4,000 objects / 256 MiB within a separate 10-second capture budget; large or ambiguous checkouts require review. Evidence means no observed recent activity, not proof that no external program cares about the checkout.

## Integrating another part of the app

Attach an `AutomaticCleanupRule` to an `Opportunity` through `IOpportunityExtension`. Rules identify a stable provider, user-facing consequences, narrow suffixes, age and supported `GeneratedResourceKind`. The default kind is `Unverified` and cannot authorize eviction. A new producer format needs host proof support; extensions do not supply arbitrary deletion callbacks.

`OptimizationEvidence` separates the action, disposition, owner, producer, variant, restoration cost, required inputs and coverage. Eviction, duplicate discovery, compression, block sharing, native cache maintenance and checkout retirement have separate consequences and admission checks. A protected checkout can still contain an independently verified disposable cache, while Git's shared metadata stays protected.

Use `GeneratedFileOwnership.Record` after a producer successfully writes an owned cache resource. Receipts bind exact contents and source revisions; they do not confer ownership over a directory. Changing patterns, source requirements, contract kind or consequences changes per-rule consent and requires selecting the revised rule again.

The [research and adversarial findings](optimization-research.md) explain the motivating precision failures and primary-source contracts. Runtime validation uses controlled local fixtures; it does not establish real-project rebuildability, actual private-feed availability or ReFS behavior without supported hardware.
