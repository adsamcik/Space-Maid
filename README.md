# Space Maid

A Windows app for finding what fills your drives and cleaning up development clutter. Built with .NET 10, WPF and the Windows Fluent theme.

- **Explore** — a sortable folder tree and treemap, with live scan results.
- **Opportunities** — grouped build output, caches, dependencies and Git worktrees, with configurable activity filters and live checks before cleanup.
  Includes [AI tool history, checkpoints and local model storage](docs/ai-storage-coverage.md), with tool-specific advice and protected shared stores.
- **Auto optimize** — opt-in cleanup of old generated files, shared across extensions, with per-rule controls, exclusions, exact previews and run limits. [Automatic optimization](docs/automatic-optimization.md) describes coverage and safeguards.
- **Storage tools** — resumable [duplicate discovery and content-preserving compression/block sharing](docs/duplicates-and-storage.md), native package-cache maintenance, and enrolled worktree quarantine with recovery grace.
- **Apps** — installation folders, measured sizes and quick access in Explorer.
- **Photos** — collections, exact duplicate review and related RAW/JPEG/sidecar files.
- **Extensions** — trusted local workflows, including an optional local assistant.
  [Agent cleanup](docs/ai-storage-coverage.md), [Containers & WSL](docs/containers-and-wsl.md) and AI models provide previews and confirmed in-app cleanup, including old transcripts/logs, Cursor CLI chats, local OpenCode sessions and virtual-disk compaction.

## Download and run

Download the **Setup** installer from [Releases](https://github.com/adsamcik/Space-Maid/releases) and choose **Install** or a **Portable** folder. Setup detects an existing installation and offers **Update**, keeping settings and photo catalogs. If the same or a newer version is installed, Setup offers to open it. Normal installation adds the .NET 10 Desktop Runtime when needed. Portable mode requires the [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) already installed. A portable ZIP is also available: extract it and open `Space Maid.exe`. Keep the extracted folders together. Existing portable apps update through **More → Updates**.

Release binaries are currently unsigned.

Release builds check this repository's GitHub Releases daily when enabled, including while the app stays open. In **More → Updates**, enable **Download updates automatically** to prepare updates in the background, or download them manually. Automatic downloads wait until both the release publication and the update package's last change are at least 24 hours old; manual downloads remain available during this protection period. Choose **Restart and update** when ready; active jobs must finish first, and settings and photo catalogs are retained. Downloads use HTTPS, stay on GitHub's release hosts and are checked against the release's SHA-256 and size before installation. Older versions are rejected; stable builds ignore prereleases. Source builds do not check for updates.

Enable fast scanning when prompted to authorize the read-only NTFS helper. The app stays unelevated; standard scanning remains available if you decline.

The same helper accelerates folder scans, cleanup review, rechecks and app-size refreshes with scoped NTFS metadata queries. Rechecks use cached file identities and the NTFS change journal to reread changed folders and new branches, while checking current sizes in batches. A verified move to the Recycle Bin can reuse the folder's existing tree. Journal gaps, hard-link changes and unsupported queries fall back to focused metadata reads; cleanup safeguards remain live. No background rescanning is required.

The host's shared `FileOperations` API chooses the available metadata backend per request. Scanning, folder measurement, cleanup review/planning, worktree and build-activity checks, refreshes and extension metadata discovery use that service. Photo discovery streams bounded batches of current directory metadata; size measurements can use scoped NTFS queries. Recycling, emptying the bin and making files online-only use their Windows Shell or sync-provider APIs through the same entrypoint, retaining confirmation and live identity checks. Callers do not select backends or request elevation themselves.

Folder and agent-file cleanup move the confirmed items to the Recycle Bin. Space is released when the bin is emptied; emptying it is a separate, permanent action. Recent build output is protected by default. Cleanup of original photos is currently disabled.

## Build from source

On Windows, install the .NET 10 SDK version specified in [global.json](global.json):

```powershell
dotnet run --project src/SpaceMaid.App -c Release
```

[Contributing](CONTRIBUTING.md) covers builds, tests, bug reports and releases. [CI](.github/workflows/ci.yml) builds on Linux and runs Windows integration tests. Version tags such as `v1.2.3` or `v1.2.3-beta.1` trigger the [release workflow](.github/workflows/release.yml), which publishes an installer, portable ZIP, update feed and checksums after those checks pass.

## Details

- [Scanner, cleanup and validation history](docs/user-guide-and-validation-history.md)
- [Extension and Photos architecture](docs/extensions-and-photos-design.md)
- [Create an Opportunities extension](docs/opportunity-extensions.md)
- [Local assistant setup](docs/local-assistant.md)
- [Inventory evidence and accounting](docs/inventory-evidence.md)

Scans can be incomplete, and observed sizes are estimates of potential recovery. Cloud online-only actions depend on provider support. Only install extensions you trust: they run with your account's permissions.

## License

Original code and project-authored assets use [MIT](LICENSE). Dependencies and the imported format registry have separate terms. The registry's mixed provenance includes unresolved reuse permissions; MIT does not cover that data. See [third-party notices](THIRD_PARTY_NOTICES.md) before redistribution.
