# Automatic optimization

The next design priorities and current precision gaps are documented in [storage optimization research](optimization-research.md). Those proposals have not expanded the automatic rules below.

Scan a location, then choose **Auto optimize**. Select the generated-file rules you want, preview exact paths and sizes, and use **Delete previewed files** for a single run. To repeat cleanup, select **Optimize automatically** and **Save settings**. Nothing is enabled by default.

Optimization permanently deletes matching files to release their storage; it does not use or empty the Recycle Bin. Regeneration replaces undo, so choose rules only where you can recreate the files. Folders and files outside each rule's patterns stay in place; original-photo locations and catalog records are outside the supported cache roots. Mixed build outputs can still contain unique matching files, including handwritten generated-looking source; the research documents this current admission gap. Ordinary cleanup retains its existing Recycle Bin behavior.

The default protects activity within 14 days and limits a run to 500 files and 512 MiB, using the larger of logical and observed allocated size for the byte limit. Each rule also has a minimum age. These are limits per run, not a target to fill. Larger files stay for manual review. Exclude any file or folder by absolute path; excluding a folder also protects its contents.

These are the currently detected patterns and expected regeneration under normal producer conditions. They do not prove ownership or rebuildability of every matching file; the research lists the missing admission checks.

| Rule | Detected candidates | Expected regeneration |
| --- | --- | --- |
| Python bytecode | `.pyc` / `.pyo` inside a recognized `__pycache__`, with matching `.py` metadata available | A compatible Python can recompile readable, valid source; current checks do not establish all these conditions |
| .NET intermediate caches | `.cache`, `.g.cs`, `.g.i.cs`, `.AssemblyInfo.cs`, `.AssemblyAttributes.cs` inside `obj` beside a project file | The matching producer rebuilds its own intermediates when required inputs and tools remain available |
| mypy type-check caches | `.data.json` / `.meta.json` inside a recognized `.mypy_cache` | mypy recreates incremental analysis |
| Photo previews | `.png` inside Space Maid's own `thumbnails-v1` cache | Space Maid renders its generated previews from available originals; unfamiliar copied PNGs have no such producer proof |

The next import, build, type check or preview may take longer. Offline originals must become available before their previews can regenerate. Dependencies, downloaded models, worktrees, agent conversations, test evidence, personal settings, tool-managed stores and generic build folders have no automatic rule. A folder name or an old timestamp alone does not make a file disposable.

Automatic runs are attempted after scans and hourly while the configured scanned location remains open. A lightweight five-minute timer checks whether a run is due; disabled mode has no timer. Scans, cleanup, extension jobs and the optimizer's settings window pause automatic runs. There is no Windows background service and no work while the app is closed. Changing the open scan to a different location does not extend the configured scope. Saving settings in another scanned location explicitly changes that scope.

Before deleting anything, the host rechecks current detection rules, folder coverage and build activity. Even logical-only previews capture native file identities. Execution checks file identity, volume, timestamps, additional streams and current attributes before deleting through the exact file's handle. New, changed, open, read-only, linked, offline, shared or otherwise protected files are kept. Current ancestor leases do not yet provide the intended rename protection, and Python source checks establish metadata availability rather than data readability; see the research's direct checks and priorities. Cancellation stops before the next file; completed deletions remain. Directories are never recursively deleted.

NTFS inventories refresh affected metadata after deletion. On filesystems providing only logical inventories, a new scan is required to update estimates and resume unattended runs. Displayed removed bytes describe file contents, not a guarantee about free space: hard links, alternate streams, compression and other filesystem behavior affect allocation. Shared files are excluded by a live link-count check.

Settings and the last run summary are stored locally in `optimization-settings.json`. Per-file outcomes are recorded under the app's `Logs/Optimization` folder, keeping the latest ten logs. A writable settings file and activity log are required before mutation. Invalid settings disable unattended runs. Failures are not immediately retried; the hourly interval applies to attempts too.

## Integrating another part of the app

All parts use the same system: implement the existing `IOpportunityExtension` contract and attach an `AutomaticCleanupRule` to a detected `Opportunity`. The contribution supplies a stable ID, user-facing name, regeneration consequences, exact generated-file suffixes and a minimum age. It does not supply a deletion callback. Keep mixed or uncertain content manual; use `CleanupBlockReason` for storage owned by another tool.

```csharp
finding with
{
    AutomaticCleanup = new AutomaticCleanupRule(
        "compiled-preview", "Compiled previews",
        "The app regenerates previews from available originals.",
        [".preview-cache"], minimumAgeDays: 7)
};
```

The host stamps provider ownership and derives per-rule consent from that identity plus a signature of patterns, minimum age, source requirements and regeneration consequences. Enabling one provider does not authorize another. Changing those semantics requires selecting the updated rule again. Providers must positively identify the generated storage; broad suffixes in arbitrary user folders are inappropriate. Shared planning, limits, exclusions, current classification, identity checks, scheduling and execution remain host-owned.
