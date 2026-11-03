# Optimize space

## Cleanup rules and weekly runs

The **Cleanup** workspace shows the selected location, cleanup rules and their estimated effect together. **Recycle folders** is the approval for that captured run; there is no second confirmation or folder-by-folder dialog. **Delete builds permanently** is a separate explicit policy for verified rebuildable output. Recycling never falls back to permanent deletion.

**More cleanup options** contains caches, worktrees, exclusions and other discovered manual rules. **Inspect folders** opens a summary first, with project/type/inactivity grouping and a paged exact-path list. **Skip this run** leaves saved rules unchanged; **Always exclude** changes the draft and takes effect in future runs only after saving it. Exclusions also protect containing cleanup targets.

**Save & schedule** supports manual reuse or a weekly run. **Ask me first** prepares a review of captured folders. **Clean automatically** explicitly authorizes future matching verified builds within the saved scope, retention, exclusions and run limits. Cache folders, worktrees and other manual rules require review; the separate verified generated-file optimizer below retains its existing precise producer contracts and consent.

Weekly schedules use Windows Task Scheduler under the signed-in user, without elevation, stored passwords, waking the PC or catching up missed times. Space Maid need not have its main window open. A changed PC time zone or failed task registration leaves the schedule inactive with an explanation. Saving settings never immediately starts cleanup. **Pause** affects future runs; **Stop cleanup** ends the active run after any operation already in progress has reported its result.

All runs use the same host-owned executor. A changed, locked, linked, unavailable or unverified folder is kept while other eligible folders continue. Fresh checks can narrow approval, but cannot add replacement folders or new matches. **Update estimate** explicitly captures a new cohort. Conflicting automatic deletion policies keep the overlapping target.

Run manifests and per-target attempt receipts are stored in the app's per-user `Cleanup` data folder through `DiskAccess`. Interrupted work never resumes automatically. An attempted operation without a confirmed outcome remains **Needs checking**, and is protected from automatic retry. History remains inspectable from its frozen receipts while the original drive is offline. Diagnostic reports omit full paths unless requested.

Automatic folder runs default to at most 10 GiB, 500 folders and 30 minutes; these are ceilings, not recovery promises. Existing native subtree limits and live file-safety checks remain in force. Recycled data continues using drive space until the user empties the Recycle Bin.

## Generated files and native tools

Choose **Optimize space** to browse actions grouped by the extension that provides them. Native tool inspection and extension workflows are available without a scan. Scan a location to add its generated-file actions; scanning pauses optimization until the results are ready.

Select a generated-file action and choose **Review files**. This checks the exact files without enabling automatic cleanup. Review the files and kept results, then use **Delete previewed files** for one run. After deletion, **Scan again…** refreshes the location. Observed folder sizes are not advertised as guaranteed recovery.

Package-cache actions use the installed tool through the host's fixed native adapters. **Inspect tool** resolves its version and configured locations without cleanup. Supported npm/pnpm maintenance requires a fresh review and explicit confirmation of all affected locations. NuGet, Cargo and Gradle remain inspection-only when no selective maintenance contract is supported. File exclusions block native maintenance if any affected scope overlaps an excluded location. Native commands have their own consequences and are not bounded by generated-file byte caps.

Docker, WSL, model and agent-history actions open the owning extension at the relevant workflow. Inspection and confirmation remain there, retaining its selection, safeguards and native tooling. These actions never become unattended cleanup. Native extension activity pauses host optimization, and foreground host work disables the extension workspace until it finishes.

In **Automatic cleanup**, explicitly enable the generated-file rules you want, choose retention and run limits, and save settings. All automatic actions are off by default. Native-only browsing without a scan preserves the saved automatic scope and rule consent. Exclusions can still be edited there.

**Save exclusions** preserves the last saved automatic location and rule consent, including when browsing another drive. **Save automatic settings** explicitly configures the location shown in that tab; a warning identifies an existing schedule for another location. Existing automatic cleanup can be turned off without scanning again.

Generated-file eviction permanently deletes verified resources; regeneration replaces undo. Ordinary folder cleanup still uses the Recycle Bin. Keep a file or its containing resource with **Keep file** / **Keep folder**, then save the exclusion for automatic runs.

Under **Excluded locations**, use **Add folders…** or **Add files…** to browse for exclusions. Folders include their contents. Remove individual entries with **Remove**, then preview again and save settings.

These file exclusions apply to generated files and host-managed native cache maintenance. Extension-owned workflows retain their own review and selection controls.

## Producer evidence

Names and age only identify candidates. The host requires a supported producer contract, readable inputs, current native identities and exact content verification before eviction. Unknown formats and incomplete evidence remain protected.

| Resource | Required evidence | Kept |
| --- | --- | --- |
| CPython 3.11–3.14 checked-hash bytecode | Direct recognized `__pycache__` child, matching magic and code marker, readable source and exact checked source hash | Timestamp caches, orphans, unchecked hashes, mismatches, unsupported versions/layouts |
| .NET compiler fingerprints | Known SDK project, exact Debug/Release and target-framework/RID layout, 64-character input fingerprint and exact FileListAbsolute producer entry | Generated-looking source, arbitrary `.cache`, binaries and unknown variants |
| mypy JSON cache | Paired 1.x metadata/data, exact data and source hashes, readable current source and bounded matching dependency records | Metadata records, unfamiliar JSON, unavailable/external dependencies and unsupported formats |
| Space Maid photo previews | Exact producer receipt binding output SHA-256 to readable unchanged originals | Unfamiliar copied PNGs, missing receipts and unavailable/changed originals |

These contracts establish resource ownership and the inputs required by the supported cache format. Space Maid does not execute project code, install build tools or guarantee that a future build succeeds. The next import, build, type check or preview may take longer. Files outside these precise contracts remain available for review or content-preserving storage actions.

Timestamp Python headers contain modification time and source length, which can still match edited source. Native regression fixtures reproduced this case, so automatic eviction requires the checked source hash. The revised Python rule needs fresh consent. Optional native tests compile owned fixtures with existing uv-managed CPython 3.13/3.14 and verify exact checked-hash regeneration; they never execute project modules. Native npm verification and pnpm 10 pruning use isolated offline fixtures. Pruning must remove unused content and metadata while preserving live hardlinks and exact project, archive and installed-file bytes. ReFS execution still requires supported storage.

## Bounds and execution

Defaults retain activity within 14 days, remove at most 500 files / 512 MiB, examine at most 20,000 inventory entries and read at most 32 MiB of proof data. Each metadata/proof phase has a 20-second limit checked between bounded operations. One proof record is limited to 8 MiB and 16 held inputs. Limits are ceilings, not targets. The byte mutation limit uses the larger of logical and observed allocation size.

A saved cursor advances through the inventory even when early candidates cannot be evicted. **Continue preview** visits the remaining inventory and stops at the end; a new **Preview** starts another sweep. A proof-budget failure still leaves the affected file unverified and the page marked partial, even when no later files remain. Sharing violations receive a short revision-specific cooldown. Scope changes, rule changes and explicit exclusions constrain subsequent work; provider consent includes the producer contract version.

The host reopens exact files and required inputs, checks volume/file IDs, revisions, contents, streams, link counts and availability, and holds effective directory-list and data leases through the operation. Metadata-only opens cannot substitute for readable source or directory rename protection. Reparse, recall/offline, shared, read-only, encrypted, replaced and otherwise unsupported files stay. Generated-file eviction never recursively deletes directories.

Output verification suppresses access-time updates for its own native handle using Windows' documented `SetFileTime(-1)` sentinel, without restoring timestamps or suppressing other readers. This requires metadata attribute access; if unavailable, the file is kept. Repeated previews therefore do not make their own candidates appear newly used. [Windows per-handle access-time suppression](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfiletime).

Automatic runs are attempted after scans and hourly while the configured location remains open. A five-minute timer checks whether a run is due. Scans, cleanup, extension jobs and storage dialogs pause automatic work. There is no background service or work while the app is closed. Changing the open scan does not extend the saved scope.

NTFS inventories refresh affected metadata after mutation. Logical-only inventories require a new scan before unattended work resumes. Removed logical contents are reported separately from guaranteed physical reclaim. Cancellation stops subsequent files; completed deletions remain.

Settings, cursor, cooldowns and the last summary live in `optimization-settings.json`. Per-file outcomes live under `Logs/Optimization`, retaining the latest ten logs. Writable settings and an activity log are required before mutation. Invalid settings disable unattended work, and attempt throttling applies to failures too.

## Other optimization actions

**More → Duplicates and storage** provides resumable identity-aware hashing and byte comparison, explicit NTFS compression of cold build resources, and capability-gated ReFS block sharing. Both names and payloads remain. See [duplicates and storage](duplicates-and-storage.md) for supported files, bounds and hardware limitations.

**More → Tool cache maintenance** probes installed npm, pnpm, NuGet, Cargo and Gradle using fixed native commands and local metadata. Supported npm 9–11 verification and pnpm 9–10 pruning require a fresh plan and explicit confirmation. The plan binds the launcher/version and every location it can change, shows the exact command and warns about later download/private-feed costs. pnpm pruning affects both the package store and a separate metadata cache: both must have known paths and native identities, remain pinned during execution and appear in the confirmation. pnpm does not report its default metadata location; configure an explicit `cache-dir` and inspect again if it is unknown. pnpm 11 remains protected because its pruning also cleans global installation data. Commands run with bounded output/time in a neutral guarded directory; project scripts, package installs and project evaluation are excluded. Unsupported versions, wrappers or roots are protected. NuGet selective eviction and Cargo/Gradle project-cache deletion remain unavailable when the tool cannot prove a safe selective action. Old age and a lockfile alone do not authorize package removal.

**More → Managed worktrees** separately checks upstream retention, an explicitly selected integration reference, patch equivalence, ignored/untracked content and operation/activity evidence. Age alone never retires a checkout. Enrollment requires a named branch and a reproducible linked checkout whose exact HEAD is retained in the configured integration target. Default inactivity is 30 days with seven days of recovery grace; automatic retirement is an additional opt-in.

Retirement creates a durable recovery ref and record, then uses Git's worktree move to quarantine on the same volume. Quarantine still occupies space. Recovery can restore the checkout while it remains unchanged. After grace, finalization rechecks Git state and deletes only captured unchanged objects through native handles, retaining branches and shared Git storage. New/changed files, missing coverage, active operations, ignored data and unsupported move conditions stop retirement. No force or broad prune fallback is used. A bounded metadata preflight precedes inventory creation; each assessment has a 20-second deadline. The content ledger admits at most 4,000 objects / 256 MiB within a separate 10-second capture budget; large or ambiguous checkouts require review. Evidence means no observed recent activity, not proof that no external program cares about the checkout.

## Integrating another part of the app

An extension can implement the optional `IOptimizationExtension` capability in `SpaceMaid.Extensions.Contracts`. Return stable action metadata identifying either a fixed `ToolCacheKind` adapter or an owning workflow ID. Declarations are metadata-only: do not launch tools, inspect files or start jobs while returning them. The host validates bounded declarations per extension and isolates invalid providers.

For an owning workflow, its retained view implements `IOptimizationWorkflowView`. `OpenOptimizationWorkflow(id)` only selects/focuses the relevant workflow; it must reject unknown IDs or busy/cancelled views and must not start inspection or mutation. `IsOptimizationBusy` includes hidden nested workflows so host cleanup and updates can wait. Generated-file actions continue to come from scanned `IOpportunityExtension` findings, without a second detection pipeline.

Attach an `AutomaticCleanupRule` to an `Opportunity` through `IOpportunityExtension`. Rules identify a stable provider, user-facing consequences, narrow suffixes, age and supported `GeneratedResourceKind`. The default kind is `Unverified` and cannot authorize eviction. A new producer format needs host proof support; extensions do not supply arbitrary deletion callbacks.

`OptimizationEvidence` separates the action, disposition, owner, producer, variant, restoration cost, required inputs and coverage. Eviction, duplicate discovery, compression, block sharing, native cache maintenance and checkout retirement have separate consequences and admission checks. A protected checkout can still contain an independently verified disposable cache, while Git's shared metadata stays protected.

Use `GeneratedFileOwnership.Record` after a producer successfully writes an owned cache resource. Receipts bind exact contents and source revisions; they do not confer ownership over a directory. Changing patterns, source requirements, contract kind or consequences changes per-rule consent and requires selecting the revised rule again.

The [research and adversarial findings](optimization-research.md) explain the motivating precision failures and primary-source contracts. Runtime validation uses controlled local fixtures; it does not establish real-project rebuildability, actual private-feed availability or ReFS behavior without supported hardware.
