# Space Maid

A .NET 10 Windows desktop app for finding development clutter and reviewing potential cleanup. The WPF interface uses the built-in Fluent theme and follows Windows light/dark settings.

**This release is read-only.** Select findings and choose **Review selection** to see exact paths, estimated candidate space, and cautions. **Clean up** is disabled. The core rejects cleanup execution too. There are no file-deletion, cache-clearing, worktree-removal, or cleanup subprocess implementations.

## Run

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project src/SpaceMaid.App -c Release
```

At startup, choose **Enable fast scanning** to reopen through Windows UAC, or **Continue without admin**. Cancelling UAC leaves the original app open. Choose a drive and click **Scan drive**. Cancellation is checked between metadata batches and during analysis.

The framework-dependent build can also be opened directly:

```text
src/SpaceMaid.App/bin/Release/net10.0-windows/SpaceMaid.App.exe
```

## What it finds

- .NET `bin`/`obj` beside a project file; Node dependencies and possible build output beside `package.json`.
- Python environments identified by `pyvenv.cfg`, and bytecode caches containing `.pyc` files.
- Standard NuGet, npm, pip, and Gradle cache locations.
- Codex logs/session history, Claude Code debug files, and linked Git checkouts identified by a `.git` pointer file.

Findings are evidence for review, not proof that every file is disposable. Linked checkouts can be worktrees or submodules. Their source changes and untracked files are not audited, and age never establishes abandonment. Custom cache locations and other agent/tool storage layouts are not automatically recognized in this first release. Detection uses metadata, not file-content inspection.

## Scanner and accounting

Eligible whole local NTFS volumes use Windows' documented [`FSCTL_QUERY_FILE_LAYOUT`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/fsctl-query-file-layout) with names, data streams (including streams without clusters), and extra metadata. Acquisition is serial, with a reusable pooled buffer and bounds-checked relative-offset parsing. Unsupported access or layout data falls back to buffered directory enumeration.

The standard backend uses `FileIdExtdDirectoryInfo`, retaining 128-bit identities, logical sizes, and reported allocation. Providers that do not expose usable identities fall back to basic enumeration, with logical-size-only and hard-link limitations disclosed. Standard scanning does not fully enumerate alternate data streams. Junction/symbolic-link targets are not followed; inaccessible locations produce coverage warnings. No content is read to hash duplicates or hydrate cloud files. A provider can still do its own work in response to metadata queries.

File objects and name links are separate compact records; basenames share a character arena. Full paths are constructed only when needed. Parent resolution and bottom-up aggregation avoid repeatedly walking ancestors. Results are virtualized, and scanning/analysis run off the UI thread. Scans remain in memory; there is no persistent index, journal creation, service, telemetry, or network upload.

Observed allocation is attributed once per file object. Candidate plans collapse nested selections and exclude allocation with observed names outside the selection. External hard links, incomplete scans, cloud/provider storage, and changes after scanning can reduce actual savings. Resident data and filesystem metadata can also prevent reported per-file allocation from becoming freed volume clusters. Logical size, observed allocation, drive usage, and reclaimable space are different quantities. Estimates are never a promise of space freed.

## Check and measure

```powershell
dotnet build SpaceMaid.slnx -c Release -m:1
dotnet test tests/SpaceMaid.Tests -c Release -m:1
dotnet run --project tools/SpaceMaid.Bench -c Release -- 'G:\Github\Space-Maid' --standard
# From an administrator terminal, benchmark a whole NTFS volume:
dotnet run --project tools/SpaceMaid.Bench -c Release -- 'G:\'
```

The benchmark prints the actual backend, metadata/aggregation elapsed time, allocation traffic, peak working set, coverage flags, and warnings. Compare equally complete work and distinguish warm from cold scans. A small folder scan does not establish whole-drive speed or parity with WizTree.

On 10 October 2026, one standard metadata scan of `G:\Github` returned 3,898,239 file objects and 3,932,055 names in 174.44 seconds: 174.12 seconds acquisition and 0.314 seconds aggregation. Peak working set was 1.31 GiB; managed allocation traffic was 2.61 GiB (not retained memory). It reported 100 coverage warnings, including un-followed junction/link targets. Cache state was uncontrolled. This measures the folder fallback only, excludes complete alternate-stream/external-link coverage, and does not time detection or UI presentation. It establishes neither whole-volume nor bulk NTFS speed.

Focused tests exercise native standard scanning on disposable NTFS fixtures, real hard links and sparse files, metadata parser rejection/accounting, detection evidence, nested/shared cleanup estimates, cancellation, and core read-only enforcement. One off-screen WPF smoke test exercises scan/selection wiring, review composition, disabled cleanup, and light/dark rendering; its images are written to ignored `artifacts/ui/`.

Current verification does **not** establish actual UAC acceptance/cancellation, elevated NTFS acquisition, non-NTFS/provider behavior, whole-volume performance, or desktop accessibility. The host used for initial development was not elevated. Windows version/architecture coverage has not been separately established. No deployed site or installer is included.
