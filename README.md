# Space Maid

A Windows app for finding what fills your drives and cleaning up development clutter. Built with .NET 10, WPF and the Windows Fluent theme.

- **Explore** — a sortable folder tree and treemap, with live scan results.
- **Opportunities** — grouped build output, caches, dependencies and Git worktrees, with configurable activity filters and live checks before cleanup.
- **Apps** — installation folders, measured sizes and quick access in Explorer.
- **Photos** — collections, exact duplicate review and related RAW/JPEG/sidecar files.
- **Extensions** — trusted local workflows, including an optional local assistant.

## Download and run

Download the Windows x64 ZIP from this repository's **Releases**, extract it and open `SpaceMaid.App.exe`. Install the [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first. Keep the bundled helper, extensions and notices alongside the app.

Enable fast scanning when prompted to authorize the read-only NTFS helper. The app stays unelevated; standard scanning remains available if you decline.

Cleanup moves selected folders to the Recycle Bin after review and confirmation. Space is released when the bin is emptied; emptying it is a separate, permanent action. Recent build output is protected by default. Photos cleanup is currently disabled.

## Build from source

On Windows, install the .NET 10 SDK version specified in [global.json](global.json):

```powershell
dotnet run --project src/SpaceMaid.App -c Release
```

[Contributing](CONTRIBUTING.md) covers builds, tests, bug reports and releases. [CI](.github/workflows/ci.yml) builds on Linux and runs Windows integration tests. Version tags such as `v1.2.3` or `v1.2.3-beta.1` trigger the [release workflow](.github/workflows/release.yml), which publishes a Windows ZIP and SHA-256 checksum after those checks pass.

## Details

- [Scanner, cleanup and validation history](docs/user-guide-and-validation-history.md)
- [Extension and Photos architecture](docs/extensions-and-photos-design.md)
- [Create an Opportunities extension](docs/opportunity-extensions.md)
- [Local assistant setup](docs/local-assistant.md)
- [Inventory evidence and accounting](docs/inventory-evidence.md)

Scans can be incomplete, and observed sizes are estimates of potential recovery. Cloud online-only actions depend on provider support. Only install extensions you trust: they run with your account's permissions.

## License

Original code and project-authored assets use [MIT](LICENSE). Dependencies and the imported format registry have separate terms. The registry's mixed provenance includes unresolved reuse permissions; MIT does not cover that data. See [third-party notices](THIRD_PARTY_NOTICES.md) before redistribution.
