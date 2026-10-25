# Space Maid

A Windows app for finding what fills your drives and cleaning up development clutter. Built with .NET 10, WPF and the Windows Fluent theme.

- **Explore** — a sortable folder tree and treemap, with live scan results.
- **Opportunities** — grouped build output, caches, dependencies and Git worktrees, with configurable activity filters and live checks before cleanup.
  Includes [AI tool history, checkpoints and local model storage](docs/ai-storage-coverage.md), with tool-specific advice and protected shared stores.
- **Apps** — installation folders, measured sizes and quick access in Explorer.
- **Photos** — collections, exact duplicate review and related RAW/JPEG/sidecar files.
- **Extensions** — trusted local workflows, including an optional local assistant.
  [Agent cleanup](docs/ai-storage-coverage.md), [Containers & WSL](docs/containers-and-wsl.md) and AI models provide previews and confirmed in-app cleanup, including old transcripts, logs and virtual-disk compaction.

## Download and run

Download the **Setup** installer from [Releases](https://github.com/adsamcik/Space-Maid/releases) and choose **Install** or a **Portable** folder. Normal installation adds the .NET 10 Desktop Runtime when needed. Portable mode requires the [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) already installed. A portable ZIP is also available: extract it and open `Space Maid.exe`. Keep the extracted folders together.

Release binaries are currently unsigned.

Release builds check this repository's GitHub Releases for updates once a day when enabled. Choose **Download**, then **Restart and update** when ready; settings and photo catalogs are retained. Prerelease builds also receive prereleases. Source builds do not check for updates.

Enable fast scanning when prompted to authorize the read-only NTFS helper. The app stays unelevated; standard scanning remains available if you decline.

The same helper accelerates folder scans, cleanup review, rechecks and app-size refreshes with scoped NTFS metadata queries. Current folder membership and cleanup safeguards are still checked; unsupported queries fall back to standard metadata reads.

The host's shared `FileOperations` API chooses the available metadata backend per request. Scanning, folder measurement, cleanup review/planning, worktree and build-activity checks, refreshes and extension metadata discovery use that service. Photo discovery streams bounded batches of current directory metadata; size measurements can use scoped NTFS queries. Recycling, emptying the bin and making files online-only use their Windows Shell or sync-provider APIs through the same entrypoint, retaining confirmation and live identity checks. Callers do not select backends or request elevation themselves.

Folder and agent-file cleanup move the confirmed items to the Recycle Bin. Space is released when the bin is emptied; emptying it is a separate, permanent action. Recent build output is protected by default. Photos cleanup is currently disabled.

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
