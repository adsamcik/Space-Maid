# Storage optimization research and implementation priorities

Space Maid should optimize exact resources using evidence appropriate to each action. The highest-value direction combines producer-aware build cleanup, exact duplicate discovery, preservation options such as compression, and worktree lifecycle management. Broad detection can find opportunities; automatic action needs narrower proof. A valuable checkout can remain protected while independently verified compiler caches inside it are reclaimed.

This research follows two specialist tracks and an adversarial inspection, checked against baseline `61c0065` and primary documentation on 25 October 2026. The findings below describe that baseline. The subsequent implementation adds producer proofs, effective native leases, bounded continuation, content-preserving storage actions, native cache adapters and managed checkout recovery. See [automatic optimization](automatic-optimization.md) for current behavior and limitations, [duplicates and storage](duplicates-and-storage.md) for preservation actions, and the regression tests for executable evidence. Historical probes remain evidence of the original failures, not claims about the updated implementation.

## Baseline findings and direct checks

Three disposable local probes reproduced concrete weaknesses. They used metadata planning, a synthetic Git repository and empty fixture directories. No optimizer execution, real-data cleanup, network Git operation or application configuration change was performed.

| Fixture | Observed result | Required precision improvement |
| --- | --- | --- |
| Old, hand-maintained `obj/archive/Recovery.g.cs` beside a project file | `OptimizationPlanner.Create` proposed the source for automatic cleanup | Generated-looking suffixes must remain discovery hints until producer ownership and disposability are established |
| Feature HEAD equals local `origin/feature`, but is not an ancestor of `main` | The analyzer returned `Integrated = true`, target `refs/remotes/origin/feature`, status `Stale worktree` | Separate retention on a tracking ref from integration into an explicitly chosen destination |
| An empty directory held with access `0x80`, share mode `3` | Directory rename succeeded; the equivalent `0x81` handle blocked it | Attribute-only handles do not establish the claimed ancestor rename protection |

The probe project built with zero warnings/errors. Its local source, logs, retained fixtures and machine-readable results are under `artifacts/optimization-research-probe`. The .NET source case ran the planner only, so it establishes false automatic eligibility rather than an observed deletion. The Git case demonstrates ambiguous integration semantics; keeping a local branch can still preserve its commits. The directory case does not demonstrate deletion of an unrelated replacement file: existing file-ID and volume checks continue to matter.

Relevant baseline locations:

- `SpaceMaid.Extensions.Developer/Analysis/OpportunityDetector.cs`: `AutomaticRule` attaches the .NET rule to `obj`; `DotNetIntermediate` includes `.cache` and generated-looking C# suffixes.
- `SpaceMaid.Core/Optimization/OptimizationPlanner.cs`: recursive suffix admission and broad worktree ancestor protection.
- `SpaceMaid.Core/Analysis/BuildCleanupPolicy.cs`: `Authored` deliberately exempts several generated-looking C# names.
- `SpaceMaid.Core/Analysis/GitWorktreeAnalyzer.cs`: upstream selection around line 125; graph containment around line 154; activity inspection around lines 237 and 323; 4,096-path lease limit around line 353.
- `SpaceMaid.Core/Optimization/RegenerableFileDeleter.cs`: ancestor and required-source leases request attributes only. `SpaceMaid.Extensions.Photos/NativeMediaFile.cs` already distinguishes attribute discovery from effective list/data leases.

Git defines upstream through branch tracking configuration. Containment in that ref answers a different question from containment in an approved integration branch. Windows documents attribute access as exempt from file sharing checks. These contracts support the observations above. [Git branch tracking](https://git-scm.com/docs/git-branch), [Git ancestry](https://git-scm.com/docs/git-merge-base), [CreateFile sharing](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).

Other inspected limitations are important even without an execution probe. A matching Python source currently needs metadata access, not demonstrated data readability or a validated relationship to its PYC. The planner can repeatedly select the same capped prefix before discovering live locks or recent access, starving later files. Recursive live validation can examine a large cache despite a small deletion cap. These need focused regression fixtures rather than a larger general test framework.

The mixed-content problem also applies to owned cache roots. `PhotosExtension.Analyze` currently contributes every `.png` under `thumbnails-v1`, without establishing that Space Maid produced each member. A copied `original.png` is an unfamiliar file even in that directory. Require recorded ownership or a suitably narrow producer contract; add this preservation fixture alongside the handwritten `.g.cs` case. This is a code-inspection finding, not a fourth executed probe.

## Action contracts and evidence

Use a small extension of the current resource/evidence records. Do not introduce a graph database, a universal rebuild engine or an invented confidence percentage. Each action reports **eligible**, **review**, **protected** or **unknown**, with the failed or missing conditions visible. An action becomes eligible only when all its required conditions hold; a large saving cannot compensate for missing preservation evidence.

Separate the following promises:

| Action | Required evidence | Restoration consequence |
| --- | --- | --- |
| Evict a disposable accelerator | Exact owner, recognized producer/layout, current inputs available under the relevant cache contract, no authored or unknown members | Recomputes from current inputs; the next operation may be slower |
| Remove a historical build output | Exact provenance, retained source/configuration/toolchain and dependencies, appropriate output-retention policy | Rebuilding today's source may not reproduce the old artifact |
| Maintain a shared package/tool cache | Owner-native usage/reference evidence, locking, actual configured roots, offline/download policy | May require downloads or credentials; a retired private feed may make restoration impossible |
| Share storage while preserving independent files | Exact equality and a backend that preserves paths, metadata and future write isolation | Both names remain usable; capability and failure semantics govern admission |
| Remove a redundant copy | Exact equality, complete relevant file/family semantics and a retained keeper; explicit ownership/path-retention policy | Equal bytes do not make every path disposable |
| Retire a checkout | Explicit integration/retention policy, complete local-content classification, inactivity and live operation checks, retained commits and recovery details | Checkout disappearance affects tools using its path; preserving a branch does not preserve ignored local data |

A thumbnail owned by Space Maid does not need a complete historical toolchain proof. A customized generated source or old signed release does. Keep the claim and evidence proportional to the resource class. Reproducible builds require controlling relevant inputs and environment; a source file's presence alone cannot certify historical reproduction. [Reproducible build environment](https://reproducible-builds.org/docs/plans/).

Useful shared facts are: provider/rule/layout version; volume and file identity; current revision; exact owner and root; producer and output class; configuration/framework/runtime or profile/target where applicable; required input/consumer references; observation scope and completeness; restoration category; retained-copy policy; candidate action and conservative savings. Keep small owner/resource/reference records, adding fields only for an implemented action. Lack of a consumer in a partial scan is unknown, not proof of global non-use.

Discovery remains metadata-first. Content reads, build metadata parsing and native tool inspection run in bounded verification jobs through the host. Existing inventory/model evidence remains an investigation capability: model prose can prioritize or explain candidates, but cannot supply authoritative sizes, regenerability or execution permission.

## Precise build resource optimization

Identify the producer and exact variant before choosing what to evict. For .NET that includes configuration, target framework and runtime where relevant; for Cargo, profile and target architecture; for Gradle, project/task and cache type. An active Debug build should not inherently make a separately owned obsolete Release variant untouchable. Conversely, a cold directory name should not authorize all its contents.

For .NET, existing trusted build output lists and `FileWrites` are better evidence than suffixes. Treat those records as inputs to validation: restrict paths to the authorized root, reject escaping/shared/unknown entries, protect explicit source inputs and invalidate ownership after content changes. A manifest is neither complete by definition nor irrevocable deletion authority. Custom targets can create or preserve files outside ordinary layouts. [MSBuild implementation](https://github.com/dotnet/msbuild/blob/main/src/Tasks/Microsoft.Common.CurrentVersion.targets), [MSBuild incremental builds](https://learn.microsoft.com/en-us/visualstudio/msbuild/incremental-builds).

Do not run arbitrary project builds or cleaners in the background to discover ownership. `dotnet clean` executes MSBuild cleanup targets, and even evaluation-only inspection expands property functions. A repository-trusted, user-invoked build can later record producer evidence; inspection of unknown projects should prefer existing records and bounded parsing. [dotnet clean](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-clean), [MSBuild evaluation](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties), [Property functions](https://learn.microsoft.com/en-us/visualstudio/msbuild/property-functions).

Python admission should recognize the cache layout and validation mode, establish readable matching current source, and distinguish a disposable accelerator from a retained runnable artifact. An unchecked-hash PYC or unreadable source is an adversarial case, not a reason to assume all bytecode is irreplaceable. Test these contracts in fixture projects; do not invoke arbitrary interpreters or project code merely to justify deletion. [PYC validation modes](https://docs.python.org/3/library/py_compile.html), [PEP 552](https://peps.python.org/pep-0552/).

Resolve actual configured storage rather than hard-coded default folders. Cargo separates final and intermediate output classes and allows relocated/shared output roots. Its current documentation also marks internal layouts as version-dependent. Keep adapters versioned and record capability/layout evidence from the installed tool. [Cargo build cache](https://doc.rust-lang.org/cargo/reference/build-cache.html).

Prefer cache-owner maintenance where it already knows usage and concurrency:

| Ecosystem | Useful integration | Precision condition |
| --- | --- | --- |
| NuGet | Actual global-package roots and coordinated selective maintenance | Up-to-date restores may leave access markers old; age cannot prove non-use. Follow owner metadata ordering and locks |
| Gradle | Existing generated/downloaded cache retention and usage tracking | Respect shared ownership, locking and installed-version behavior |
| Cargo | Existing global-cache GC plus separately targeted build-resource actions | Global-cache tracking does not establish build-artifact use; preserve offline behavior |
| npm | Native cache verification and owner maintenance | Dependency-tree deduplication is a separate transformation |
| pnpm | Actual content-addressed store inspection and native pruning | Store pruning can require later downloads; dependency dedupe can change lockfile resolution |

NuGet specifically warns that up-to-date restores do not refresh `.nupkg.metadata`; selective removal must coordinate with its locking and remove that completion marker first. Gradle and Cargo distinguish resources recreated locally from resources requiring downloads. Those distinctions should survive in Space Maid's policy. [NuGet cache contracts](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [Gradle cache lifecycle](https://docs.gradle.org/current/userguide/directory_layout.html), [Cargo global caches](https://doc.rust-lang.org/cargo/reference/config.html#global-caches), [npm cache](https://docs.npmjs.com/cli/v11/commands/npm-cache/), [pnpm store](https://pnpm.io/cli/store), [pnpm dedupe](https://pnpm.io/cli/dedupe).

Keep downloaded models, container layers, virtual disks, application installations and agent recovery data behind their owning adapters. An adapter may expose precise retention or native maintenance actions through the same decision model; a generic filesystem sweeper should not break their references or duplicate accounting.

## Duplicate discovery and storage preservation

The pipeline should progressively spend evidence-gathering effort:

1. Coalesce names already referring to the same `(volume, file ID)`. They are aliases, not additional physical copies.
2. Group independent objects by verified content length.
3. Optionally sample to reject mismatches. A matching sample never establishes equality.
4. Hash complete content under stable handles using the existing bounded content-work pattern.
5. Reacquire exact identities/revisions and byte-compare keeper and candidate before mutation.
6. Verify the semantics needed for the proposed action: named streams, relevant attributes/security, independent mutability, companion records, retained paths and owner references.

Photos already provides bounded stable content reading and labels comparison of default streams separately from other data. Reuse that pattern in shared verification code without making the host depend on Photos implementation types. Similar images, matching basenames or semantic source similarity remain review groupings, not exact duplicate deletion authority.

Persisted hashes may accelerate discovery, but are not execution permission. Timestamp-restored edits, file-ID reuse, volume changes, journal resets/gaps and provider changes invalidate or weaken cached evidence. A change journal helps invalidate facts; it does not record reads or prove non-use. Likewise, Windows access timestamps can be delayed or disabled. [Change journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals), [Windows file times](https://learn.microsoft.com/en-us/windows/win32/sysinfo/file-times).

Avoid generic NTFS hardlink consolidation across mutable source/build trees. Hardlinks share one file, so future writes and attribute changes propagate through its names. Matching bytes today do not establish lifetime immutability; a read-only bit is not an ownership contract. Tool-managed immutable stores are a different, explicit case. [Hardlink semantics](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions).

Two preservation actions deserve a later, measured pilot:

- **Per-file NTFS compression:** consider cold, compressible build artifacts that should remain available. Preserve contents and names, verify payloads, and measure allocation and foreground performance. Avoid blanket directory inheritance and exclude active/provider/database/VM stores initially. This is a candidate optimization, not a guaranteed saving. [NTFS compression](https://learn.microsoft.com/en-us/windows/win32/fileio/file-compression-and-decompression), [Compression control](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression).
- **Capability-gated ReFS block cloning:** share compatible extents while preserving independent files through allocate-on-write. Require supported same-volume conditions and native failure/cancellation/write-isolation proof. Do not assume availability on an NTFS installation or recommend a filesystem migration for this feature. [Block cloning](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning).

Report logical redundant bytes, observed allocation, already-shared allocation and unknown physical reclaim separately. Distinct file IDs can still share cloned extents. Union object/resource identities across dedup, eviction and retirement plans so the same resource is not counted repeatedly. A volume free-space delta is useful telemetry, but unrelated writes can confound attribution.

## Worktree states and retirement

Replace a single broad integration label with separate recorded facts: exact registration, branch/HEAD retention, containment in a chosen integration ref, optional patch-equivalence evidence, local-content coverage, activity observations, operation/lease state and remote-ref freshness. Default remote HEAD can suggest a destination; it does not silently select the user's policy.

| State | Appropriate interpretation | Action direction |
| --- | --- | --- |
| HEAD is on `origin/feature`, absent from approved `main` | Retained on that tracking ref; not integrated into `main` | Clean qualified artifacts; show checkout retirement separately |
| HEAD is an ancestor of the approved integration target | Exact commits are reachable there | Retirement candidate after the other gates pass |
| Squash, cherry-pick or rebased equivalent | Similar changes may be retained with different history | Review consolidation; preserve original commit references |
| Old named branch, not integrated | Inactivity does not erase its value | Preserve branch; independently assess local checkout contents |
| Detached unique commits | No branch necessarily retains them | Protect until an explicit recovery/integration decision |
| Clean tracked state with ignored local data | Git cleanliness does not classify those files | Unknown ignored content blocks checkout retirement |
| Locked, active, missing, portable, in-progress operation or incomplete coverage | Unsafe or unknown for retirement | Preserve; show the specific reason |

Ancestry is an exact graph relationship, not proof that the changes remain effective after a later revert. Patch equivalence and squash evidence are useful review information, but cannot silently discard unique history or the owner's reason for retaining a checkout. [Git ancestry](https://git-scm.com/docs/git-merge-base), [Patch equivalence](https://git-scm.com/docs/git-cherry), [Git status](https://git-scm.com/docs/git-status).

Unattended retirement can be a useful opt-in class for managed, reproducible linked worktrees. Require a durable named reference retaining captured HEAD; the configured integration condition; complete classification of tracked, untracked and ignored content; no unique local data or in-progress Git operation; no positive recent activity or known editor/agent/build lease; complete current identity/registration/ref checks; and a recovery record. Describe evidence as **no observed activity**, rather than claiming a checkout is unused. Arbitrary unmanaged worktrees should remain reviewable even when generated-resource cleanup qualifies.

For a retirement grace period, use a Git-aware move where supported so registration follows the checkout; an ordinary directory rename alone is not a complete worktree move. Revalidate and stop when Git's move restrictions apply. Quarantine itself does not reclaim its occupied bytes. Later finalization needs current coverage and identity checks, with changed or newly introduced files retained and partial results explicit. Preserve branches/shared Git storage; do not use automatic `--force` or broad prune/delete fallbacks. Missing registrations can represent disconnected portable storage. [Git worktree lifecycle and move restrictions](https://git-scm.com/docs/git-worktree).

Improve large-repository recall by checking necessary Git identity/config/index/refs/operation metadata and streaming or resuming content classification. Do not recursively lease every shared object merely to answer HEAD containment. Retain fail-closed limits and explicit unknown coverage instead of weakening them.

## Bounded work and useful feedback

Separate mutation limits from inspected entries, bytes hashed, elapsed verification time, concurrency and retained handles. A small deletion cap does not bound a recursive scan. Use a simple cursor per provider/root and revision-keyed skip cooldowns so a locked prefix cannot starve later eligible resources. Partial examination remains resumable and visibly incomplete. Source/control changes invalidate the relevant action's proof rather than making unrelated variants repeat every check.

Rank only eligible actions, favoring meaningful conservative reclaim with low regeneration cost, no required network and low observed activity. Account for subsequent rebuild/download cost and avoid repeatedly evicting resources immediately recreated by normal use. A short cooldown and activity records can prevent thrashing without a predictive model or a new scheduling framework.

Show the resource, owner/variant, action, expected saving basis, regeneration consequence and decision reason together. Offer **keep this resource/project** near the result. Preserve existing opt-in scope, exclusions, exact previews and consolidated outcomes. Explain an unavailable action through its missing evidence; keep raw technical diagnostics in details.

## Implementation order and acceptance

| Priority | Coherent implementation | Evidence of completion |
| --- | --- | --- |
| 0 | Tighten mixed-output and Python admission; correct effective native leases; separate upstream retention from approved integration | Reproduced counterexamples become focused regression fixtures; no unsupported automatic eligibility |
| 1 | Add action-specific evidence and exact owner/variant boundaries using current provider/evidence APIs | Valuable/dirty worktree stays; its independently qualified cold cache can be reclaimed |
| 2 | Add bounded read-only general duplicate discovery and progress/skip budgets | Exact groups verified; aliases/streams/overlapping plans accounted for; later candidates make progress |
| 3 | Add precise .NET/Cargo/Gradle resource adapters and native package-cache maintenance where supported | Versioned layouts, configured/shared roots, locks and offline restoration policy exercised |
| 4 | Add an opt-in NTFS compression pilot and managed worktree retirement lifecycle | Payload/write isolation, recovery, partial cancellation and allocation evidence established |
| 5 | Add ReFS block-sharing execution where supported | Capability, concurrency, failure and independent-write integration tests pass |

The initial acceptance corpus should include:

- Handwritten or explicitly compiled `.g.cs` under an old `obj/archive`; altered generated outputs; missing custom generators, SDKs and private dependency sources.
- An unfamiliar original PNG copied into `thumbnails-v1`; only positively owned generated previews qualify for automatic eviction.
- PYC source that is metadata-readable but data-unreadable, unchecked/mismatched cache headers, missing interpreter and restored invalid source.
- Active and cold build variants sharing a project, relocated/shared output roots, unknown files mixed with known generated content.
- Feature-only upstream, normal merge, squash/rebase/cherry-pick/partial integration, a later revert, stale refs, shallow history, detached unique commits and ref changes during checking.
- Clean Git with ignored secrets, local databases, unique downloads/checkpoints; portable/locked worktrees; recent copies with old mtimes; new files during retirement finalization.
- Same-sized unequal files, matching samples with different full content, restored timestamps, identical default streams with different ADS/XMP/security, existing hardlinks and shared ReFS extents.
- Source, ancestor, target, keeper and volume replacement; effective lease tests; cancellation and partial failure.
- Large empty/unevictable caches, continually locked first pages, interrupted scans, journal gaps, unsupported tool versions and overlap across simultaneous optimization proposals.

Measure preservation errors and false automatic eligibility separately from missed valid opportunities, unknown coverage, physical reclaim, inspection I/O/time and later regeneration cost. Automatic-action precision is the hard gate; improve recall by gathering stronger evidence and adding narrower supported contracts. Zero failures in the fixture corpus is necessary evidence, not a universal safety guarantee. Validate resource contracts with controlled fixture rebuilds; this research does not establish real-project rebuildability, ReFS behavior or field performance.
