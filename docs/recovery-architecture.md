# Recovery architecture and implementation contract

This pass precedes the resilience fixes requested on 2026-11-17. It defines the shared behavior for all work items below. Preserve existing safety boundaries; recovery never grants new mutation authority.

## State and responsibility

- Keep operation outcome separate from presentation refresh. Retain immutable per-item receipts and accepted/pending/completed counts even if logging, persistence or inventory refresh subsequently fails. An accepted provider request is not confirmed space recovery.
- Keep displayed information separate from authority to mutate. Retain useful results, selection, filters, viewport and entered work on cancellation or failure, explicitly mark stale or incomplete information, and disable mutations until their existing fresh validation succeeds.
- Distinguish unsupported, unavailable, incomplete, cancelled, timed out and successfully empty states. Error details identify the affected item and a useful next action. Never infer absence from unreadability.
- Scope invalidation to possibly changed items. Preparation-only failures do not dirty inventory. A failed namespace merge retains the old snapshot and action receipts; offer the required rescan directly without repeating an impossible folder recheck.
- Retry reads, discovery and initialization only through explicit or bounded recovery. Never automatically retry uncertain mutations, substitute paths, broaden approval, weaken access flags or reuse expired mutation authority.
- Preserve unreadable durable records. Isolate their failure so unrelated records remain usable; do not overwrite unread data with defaults. Recovery is explicit, reviewable and non-destructive.
- Treat caches and optional metadata as optional. Reuse verified work only while identity/revision evidence remains valid. Bound processing without silently discarding remaining work; offer continuation or an explicit retry.

## Implementation ownership

Use existing Core records, extension contracts, host inventory and WPF state owners. Prefer small typed additions or delegating overloads at the owning boundary when required. Do not add a universal operation engine, a second filesystem facade, ambient overrides, or a new dependency. Shared behavior means consistent semantics, not forcing unrelated providers through one abstraction.

All production disk operations use the configured DiskAccess as documented in file-access.md. Preserve callable/reflected signatures. Keep mutation receipts after invalidating a preview; previews and completed results have separate lifetimes. Keep historical display separate from executable plans.

## Work items (one implementation subagent per problem)

1. Cloud action receipts survive refresh failure; coherent rescan recovery.
2. Optimization retains per-file completed results.
3. Cleanup record corruption is isolated from healthy history/definitions.
4. Drive-history read failures cannot erase unread saved history.
5. Photo keep preference has retained, visible, removable state.
6. Installer recognizes legacy/damaged destinations and offers preservation-first recovery.
7. Interrupted worktree finalization has deliberate reconciliation.
8. Thumbnail cache failure allows memory-only previews with truthful diagnostics.
9. Failed/cancelled replacement scans preserve prior inventory and context.
10. Logical-only inventory offers supported refresh recovery.
11. Cloud inspection distinguishes unavailable from unsupported.
12. Cloud browsing supports bounded continuation and honest search coverage.
13. Cloud saved-folder loading can recover after repair.
14. Cloud skipped-item reasons remain visible and truthful.
15. Cleanup validation-only failure supports fresh review without losing selection.
16. Optimization preparation failure does not invalidate untouched inventory.
17. Optimization adopts successfully refreshed inventory for the next preview.
18. Stopped worktree operations refresh durable recovery information.
19. Worktree terminal history and inactive policies can be safely retired.
20. Duplicate comparison can retry deferred/unavailable candidates safely.
21. Photo comparison completion is independent from optional metadata failure.
22. Photo analysis reuses still-valid completed work after interruption.
23. Photo empty states reflect incomplete/failed discovery.
24. Model/session refresh retains useful stale results and selection.
25. Agent-history validation identifies bad items and preserves review context.
26. Assistant interruption retains visible context without mutation authority.
27. Extension initialization/context delivery can retry without restart.
28. Scheduler verification outages do not permanently disable valid schedules.
29. Update discovery uses bounded failure backoff and visible retry state.
30. OpenCode timeout is distinct from user cancellation.
31. Remember dismissal of the startup elevation offer.
32. Persist manually added app folders across restarts.
33. Photo collections support safe root repair/removal without touching originals.

## Integration and validation

Each work item uses a lightweight sparse Git worktree sharing repository objects. Workers inspect and implement only their assigned problem, write focused behavioral tests where warranted, review their diff, and commit locally. No worker may build, run tests, invoke package restore, delegate, or mutate personal data. Related-file work is sequenced or reconciled centrally.

The coordinator integrates all completed work into main, preserves unrelated edits, obtains independent review, and then performs one bulk validation phase: DiskAccess audit, pinned Release build, focused affected unit tests, and proportionate existing desktop/system fixture checks. Rechecks after validation are restricted to concrete failures and their fixes. Compilation does not establish live provider, installer, Shell or physical filesystem behavior.

## Implementation and verification

All 33 work items were integrated locally on 2026-11-17. Each had one implementation agent and a small sparse worktree. Workers wrote tests and performed static review without building, restoring, running tests or executing native operations. Completed worktrees were removed only after checking all files and integration; local branch references remain.

Independent review identified three additional correctness gaps in the implementation: suppressed cleanup-store probes, cross-instance worktree settings writes, and legacy update timestamp interpretation. All three were corrected and independently rechecked. Runtime failures also exposed missing explicit `InvalidDataException` recovery filters; the affected corruption/recovery cases now pass.

- DiskAccess audit passed across 237 production C# files.
- Pinned Release solution build and affected dependency rebuilds passed with no warnings or errors.
- Bulk unit results and focused corrections verified 952 unique unit cases; the four affected classes passed 33/33 on recheck.
- Native/System checks verified 107 cases. Five raw-NTFS/helper/tool checks were unavailable. One unchanged Cursor recycling fixture remained blocked by the existing activity guard matching Battle.net's `Agent.exe` as a Cursor owner; that protection was not weakened and no process was stopped.
- The existing single-Application desktop harness passed. Rendered compact optimization receipts, saved-folder recovery and unavailable-photo states were visually inspected; screenshots are under `artifacts/ui`.

Real sync-provider processing/space release and actual installer execution were not exercised. Registration-only Git damage still requires deliberate manual repair followed by recheck. Replacing already-loaded extension binaries or recovering from incomplete extension disposal can still require restart. These limits preserve the existing safety boundaries.
