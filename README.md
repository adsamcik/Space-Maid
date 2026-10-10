# Space Maid

A .NET 10 Windows desktop app for finding development clutter and reviewing potential cleanup. The WPF interface uses the built-in Fluent theme and follows Windows light/dark settings.

**Bulk cleanup is preview only.** Select findings and choose **Review selection** to see exact paths, estimated candidate space, and cautions. **Clean up** is disabled and the core rejects execution of those plans. Manual folder recycling in Explore is available with a separate confirmation.

The [extensions and Photos architecture proposal](docs/extensions-and-photos-design.md) describes a possible next step. It is awaiting approval; extension loading and photo analysis are not implemented.

## Run

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project src/SpaceMaid.App -c Release
```

At startup, choose **Enable fast scanning** to reopen through Windows UAC, or **Continue without admin**. Cancelling UAC leaves the original app open. Choose a drive and click **Scan drive**. Cancellation is checked between metadata batches and during analysis.

While scanning, **Explore space** shows **Preliminary** allocated/logical totals and up to 100 largest regular file objects found so far, refreshed about once a second between metadata batches. It ranks allocation when available, otherwise logical size, and counts shared file objects once when identities are available. Basic logical-only mode discloses that hard links may repeat. Exact paths appear as parent records become available; unresolved paths are explicitly marked pending. These are partial observed sizes, not folder totals or cleanup estimates. Cancellation retains the latest bounded preview; a backend fallback clears it before the new acquisition. The full tree, treemap, and cleanup estimates replace the preview when ready. Updates do not repeatedly copy or aggregate the full inventory, and benchmarks without a progress subscriber do not maintain the preview index.

**Explore space** shows the full observed folder/file tree, sorted largest first, with allocated size, logical size, and share of the parent. Expand folders with their arrows or the keyboard's Left/Right keys. Selecting a row updates the treemap; selecting a map block drills into that location. Breadcrumbs navigate back up. Shared hard-link names stay visible, but their allocation is attributed once; additional names contribute zero extra allocation. Known incomplete branches are marked **Partial**.

Drive controls, Free/Scanned/Candidate summaries, and a free-capacity bar share a compact two-row header. Click a tree or preliminary-list column heading to sort: names start A–Z, numbers start largest first, and repeated clicks reverse the order with an arrow. Tree sorting changes siblings within each folder and preserves expansion and selection; the map remains ordered by contribution size.

Right-click a resolved folder in the tree or treemap for **Open in File Explorer** and **Move to Recycle Bin**. Right-click selects the entry and updates its details without drilling into the folder. Menu actions retain the folder on which the menu was opened; later selection or a different scan cannot redirect recycling. The keyboard context-menu key or Shift+F10 opens operations for the selected tree folder. Recycling is disabled for protected entries and outdated scans, and still uses the same live validation and confirmation. File rows, grouped map entries, unresolved entries, and blank areas do not expose folder operations.

Press **Delete** while Explore has focus to recycle its **selected folder**. One confirmation shows the exact path, observed sizes, and relevant cautions; **Keep folder** is the default. This includes the folder's current contents, even if the scan missed files or contents changed. Windows handles the Recycle Bin, progress, and cancellation on a separate STA thread so the app stays responsive. Cancellation can leave some contents already moved. There is no permanent-delete action: a Windows callback requesting permanent deletion is refused. Drive roots, filesystem metadata, grouped or unresolved selections, files, reparse paths, and folders whose identity changed are blocked. If Windows cannot verify an identity, recycling is blocked. Delete in a path text field does not recycle folders, and Shift+Delete does not enable permanent deletion.

After a recycling attempt, the retained tree and map are clearly marked **Out of date**, candidate estimates and cleanup review are disabled, and further deletion requires a rescan. Space remains occupied until the Recycle Bin is emptied; the app does not empty it. Sizes are observations, not a promise of space freed. Scanning itself remains non-destructive.

The treemap uses allocation when available and explicitly switches to logical size otherwise. It fills the available pane below one breadcrumb/size row and shows a compact selected-path/size strip. Folders contributing at least 25% can show one nested child level, with at most 64 blocks across both levels. Smaller entries are grouped visually; every observed name remains available in the tree. Parent headers and nested folders retain navigation and folder operations. Details that do not fit remain available in tooltips and the tree. Map colors reflect the existing detection rules and do not establish that files are disposable. **Opportunities** retains selection, cautions, the disabled cleanup review, and **Show in tree** navigation.

The actual backend and any fallback reason appear live below the scan status. **Open log** is available during scanning and after completion, cancellation, or failure. Each scan writes timestamped JSONL diagnostics under `%LOCALAPPDATA%\SpaceMaid\Logs`: executable/build/admin state, backend attempts and Windows errors, query/batch/object/name counts, overall object rate, native-query/parsing time, memory, coverage warnings, analysis/explorer timings, and the final outcome. There is no file-content or per-file tracing. Progress samples are written at most once per second; state changes and warnings are recorded when they occur. Logs are capped at 10 MB with an explicit limit marker, and the newest 10 are retained (an older log still open by another scan may survive until a later rotation). Logging failures are shown without stopping scanning. Logs can contain the target and skipped paths, and stay local.

**Scan details** shows the actual backend and fallback reason, files/folders/names, metadata throughput, phase timings, process peak memory, managed allocation traffic, accounting limits, and bounded coverage diagnostics. Process peak is a lifetime high-water mark, allocation traffic is not retained memory, and explorer preparation time does not include completed UI rendering.

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

File objects and name links are separate compact records; basenames share a character arena. Full paths are constructed only when needed. Parent resolution and bottom-up aggregation avoid repeatedly walking ancestors. Expanded tree branches retain compact indexes; visible display rows and recycled UI containers are created on demand. Folder sorting and map aggregation run off the UI thread and reuse the scan inventory. Scan inventories remain in memory; diagnostics are stored locally. There is no persistent file index, journal creation, service, telemetry, or network upload.

Observed allocation is attributed once per file object. Candidate plans collapse nested selections and exclude allocation with observed names outside the selection. External hard links, incomplete scans, cloud/provider storage, and changes after scanning can reduce actual savings. Resident data and filesystem metadata can also prevent reported per-file allocation from becoming freed volume clusters. Logical size, observed allocation, drive usage, and reclaimable space are different quantities. Estimates are never a promise of space freed.

## Check and measure

```powershell
dotnet build SpaceMaid.slnx -c Release -m:1
dotnet test tests/SpaceMaid.Tests -c Release -m:1
dotnet run --project tools/SpaceMaid.Bench -c Release -- 'G:\Github\Space-Maid' --standard
# From an administrator terminal, benchmark a whole NTFS volume:
dotnet run --project tools/SpaceMaid.Bench -c Release -- 'G:\'
# Add the same bounded diagnostic logs to a benchmark:
dotnet run --project tools/SpaceMaid.Bench -c Release -- 'G:\' --log-directory 'artifacts\benchmark-logs'
```

The benchmark prints the actual backend, metadata/aggregation elapsed time, allocation traffic, peak working set, coverage flags, and warnings. Compare equally complete work and distinguish warm from cold scans. A small folder scan does not establish whole-drive speed or parity with WizTree.

On 10 October 2026, one standard metadata scan of `G:\Github` returned 3,898,239 file objects and 3,932,055 names in 174.44 seconds: 174.12 seconds acquisition and 0.314 seconds aggregation. Peak working set was 1.31 GiB; managed allocation traffic was 2.61 GiB (not retained memory). It reported 100 coverage warnings, including un-followed junction/link targets. Cache state was uncontrolled. This measures the folder fallback only, excludes complete alternate-stream/external-link coverage, and does not time detection or UI presentation. It establishes neither whole-volume nor bulk NTFS speed.

After correcting the NTFS control code and version-1 unnamed DATA stream recognition, an elevated whole-volume `G:\` scan on 10 October 2026 used **Fast NTFS scan** without fallback: 4,193,946 objects, 4,228,719 names, 42.25 seconds total (41.22 seconds acquisition, 0.981 seconds aggregation), 480,182,886,432 allocated bytes, and no coverage warnings. The log measured 37.45 seconds inside metadata queries, 3.072 seconds parsing, and 1,712 parsed batches. Peak working set was 2.26 GB; managed allocation traffic was 2.55 GB. Cache state was uncontrolled; concurrent activity can affect results. This excludes detection and UI presentation, and is not an equal-scope comparison with the earlier folder scan or a measured WizTree comparison. Windows query time dominates this run; a direct MFT backend would be a separate architecture change.

On 10 October 2026, the Release solution build completed with zero warnings/errors. All 23 checks were validated: 22 scanner/analysis/logging/desktop tests, plus a separately elevated native first-batch NTFS test (explicitly skipped under a non-administrator token). Focused tests exercise native standard scanning on disposable NTFS fixtures, real hard links and sparse files, native MFT stream-size preservation, version-1/2 metadata rejection/accounting, detection evidence, nested/shared cleanup estimates, cancellation, core read-only enforcement, log retention/size limits, and logging failure handling. An off-screen native WPF smoke test exercises scan/selection wiring, tree and treemap navigation, live diagnostics/log creation, target-change isolation, scan details, review composition, disabled cleanup, minimum-size layout, and light/dark rendering. A synthetic 50,000-file folder verifies that initial layout and scrolling to its final row each retain at most 100 row controls; this checks UI virtualization, not whole-drive acquisition speed. Rendered images are written to ignored `artifacts/ui/`.

The preliminary-results change was checked with 26 passing tests and one administrator-only native test skipped, plus a successful Release solution build with zero warnings/errors. Added checks cover bounded ranking, partial hard-link accounting, late/invalid parents, immutable earlier updates, and real batch cancellation. The WPF check renders live and cancelled previews in light/dark/compact layouts, verifies they remain readable while scanning controls are busy, and checks reset/replacement behavior. The growing display uses a synthetic inventory; this change has not been timed on a whole drive with the preview enabled.

Manual folder recycling was checked with a successful Release solution build, 38 passing tests and one administrator-only acquisition check skipped, followed by 17 passing focused deletion/desktop/analysis checks after the final guard and layout edits. A final focused WPF check also passed with grouped-map deletion blocking. These exercise a real Windows Recycle Bin operation on uniquely named owned temporary folders, verify preserved contents, and restore only the exact returned fixture destination. They also check protected/ambiguous paths, replaced identities, unresolved/reparse selections, pre-operation cancellation, the permanent-fallback policy gate, keyboard confirmation/cancellation, text-field isolation, and stale-results locking. Light/dark confirmation layouts were inspected. Mid-operation cancellation and unavailable/full Recycle Bin volumes have not been separately exercised; this is not a cleanup safety audit of arbitrary user folders.

Folder context menus were checked with a Release build with zero warnings/errors and 19 passing focused desktop/explorer/deletion checks after final edits. The WPF check routes right-click and keyboard menu events through an off-screen window, displays the native popup, checks both menu locations, reveals collapsed tree ancestors without replacing the map, preserves captured confirmation targets, and blocks protected/stale or old-scan actions. The dark menu rendering was inspected. A preceding full suite passed 39 tests with one administrator-only acquisition check skipped. Physical pointer input and a newly launched File Explorer window were not separately exercised.

Current verification includes elevated G: NTFS acquisition and the stated whole-volume benchmark on this machine. It does **not** establish the application's UAC acceptance/cancellation flow, non-NTFS/provider behavior, WizTree performance parity, or desktop accessibility. Windows version/architecture coverage has not been separately established. No deployed site or installer is included.
