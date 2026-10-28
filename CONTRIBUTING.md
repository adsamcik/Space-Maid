# Contributing

Keep changes focused. Include screenshots for interface changes and describe how you checked the affected behavior. Use disposable fixtures for cleanup tests; never test deletion on personal files.

## Build and test

Use the .NET 10 SDK pinned in [global.json](global.json). On Windows:

```powershell
dotnet build SpaceMaid.slnx -c Release -m:1
./tools/Test.ps1 -NoBuild -Filter 'FullyQualifiedName~SpaceMaid.Tests.PathNavigationTests'
```

Tests have two stages:

- **Unit:** the routine default. Run only the classes or methods affected by the change, including safe owned-file fixtures. Do not run the entire suite after every edit. The test project defaults to `tools/testing/Unit.runsettings`, excluding tests marked `[Trait("Stage", "System")]`.
- **System:** Windows/UI and native integration checks that open windows, use the Windows shell or Recycle Bin, register cloud placeholders, query raw volumes, launch host helpers or sandbox workers, or require installed native tools. Run these sparingly, only when the changed behavior needs that evidence. Mark new tests with the same trait when they require this stage.

Use `tools/Test.ps1` for targeted runs: plain `dotnet test --filter` overrides the settings-file filter. The helper requires a targeted `-Filter` and combines it with the stage boundary, even when the filter contains `|`. A selection matching no tests fails. Use `-ListTests` to inspect a selection without running it. Omit `-NoBuild` when the test binaries need rebuilding.

When system verification is necessary, select the affected test:

```powershell
./tools/Test.ps1 -Stage System -NoBuild -Filter 'FullyQualifiedName~DeletionTests.WindowsRecyclesOnlyTheFixtureFolderAndPreservesItsContents'
./tools/Test.ps1 -Stage System -Desktop -NoBuild -Filter 'FullyQualifiedName~SpaceMaid.Tests.DesktopTests'
```

Run desktop tests as a normal user. Raw-volume checks require administrator access and are skipped otherwise. A successful build does not establish native cleanup, cloud-provider or desktop interaction behavior.

The system stage keeps desktop and other tests in separate test processes: use `-Desktop` for the desktop harness. WPF's process-wide pack-resource lookup cannot distinguish directly referenced extension assemblies from copies loaded into the app's extension contexts. Combining both groups can make Photos resources resolve from the wrong assembly. See the [WPF resource cache](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/AppModel/ResourceContainer.cs). Optional native Python/npm checks use existing installed tools and owned offline fixtures; they never install tools or use personal caches.

CI additionally downloads the official portable pnpm 10.34.9 executable through `tools/ci/Get-TestPnpm.ps1`, verifies its pinned SHA-256, and requires the real offline pruning test to execute. It is a test input under `artifacts/ci-inputs`, not an installed or bundled app dependency. To run the same fixture locally, set `SPACEMAID_TEST_PNPM` to that script's returned path before selecting it in the system stage. Source tarballs, installed files, configuration and cache locations all stay inside owned temporary fixtures.

For builds using `ArtifactsPath` outside the checkout, set `SPACEMAID_TEST_REPOSITORY` to the repository root before running desktop tests so screenshots and workflow diagnostics have a known destination. Run binaries from their physical output path; extension and model-worker safety checks intentionally reject junction ancestors.

Git must be available on `PATH` for worktree fixtures. For the real Photos metadata checks, install the complete ExifTool Windows bundle as `%LOCALAPPDATA%\Programs\ExifTool\ExifTool.exe`, including its companion files. CI installs its pinned version automatically; local ExifTool-specific tests skip when it is absent.

Linux can build the Windows package but cannot run WPF or Windows integration tests:

```bash
dotnet publish src/SpaceMaid.App/SpaceMaid.App.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --output artifacts/package -m:1
```

[CI](.github/workflows/ci.yml) retains its full validation gate on isolated GitHub-hosted runners: Linux builds and verifies the package, then Windows runs the same build's unit and system stages. This does not require repeating either full stage on the local desktop. Test results and desktop diagnostics are retained as workflow artifacts.

## Report a bug

Use the repository's **Issues** tab. Include the app version, Windows version, steps to reproduce and expected behavior. For scan problems, include the backend shown in **Scan details**. Logs can contain local paths; remove private information before attaching them.

## Extensions

Start with the [Opportunities extension guide](docs/opportunity-extensions.md) or [workflow architecture](docs/extensions-and-photos-design.md). Keep cleanup validation in the shared host; extension classification alone must not authorize deletion.

## Releases

Push a version tag on the intended commit:

```bash
git tag v1.2.3
git push origin v1.2.3
```

Use `v1.2.3-beta.1` for a prerelease. Tags require three version numbers, each at most 65534, and may include a SemVer prerelease suffix. The [release workflow](.github/workflows/release.yml) stamps the version and repository, runs CI, then uses the pinned Velopack tool to prepare update packages. A self-contained Setup.exe embeds the native installer, portable ZIP and full update package. Existing per-user installations are detected from their product registration or default folder and updated through `Update.exe apply`, preserving settings and catalogs beside the app. The ZIP, update feed and checksums are also published using GitHub's built-in token. Both release types use the `win` update channel; stable installations ignore prereleases.

Keep every generated update package and feed asset on the release. The app discovers its repository from the build's `GitHubRepository` property; no owner name or access token is embedded. Releases must be public for unauthenticated update checks. Older plain ZIP builds need one manual install or extraction of a new release to enable updates.

The updater requires full packages named `SpaceMaid-<version>-full.nupkg` on the matching `v<version>` release, with SHA-256 and a positive size of at most 1 GiB. Requests and redirects are restricted to HTTPS on the build's repository and GitHub's release asset host. Metadata reads are bounded to 2 MiB, downloads to 1 GiB and 15 minutes. Cached packages are checked again before restart. Automatic downloads require a saved opt-in; installation always requires **Restart and update**, after host workers stop. Update settings from older builds retain manual downloads. These checks trust the GitHub repository and HTTPS; binaries and feeds are not independently publisher-signed, so protect repository and release access accordingly.

Automatic downloads additionally require GitHub's release `published_at` and the selected package asset's `updated_at` to both be at least 24 hours old, measured in UTC. Missing dates keep automatic downloading paused; future dates extend the wait. Uploading a replacement package resets the protection period. Manual **Download update** remains available immediately. Eligibility is checked on each update check; the normal daily schedule may download later than the 24-hour boundary.

Prepare a release locally by publishing with `-p:Version=0.1.0-beta.1 -p:GitHubRepository=adsamcik/Space-Maid`, then running `tools/ci/New-ReleasePackage.ps1 -PackagePath artifacts/package -Tag v0.1.0-beta.1 -OutputPath artifacts/release-beta.1`. Use an empty output folder. Version-specific notes in `docs/releases/<tag>.md` are included automatically. Packaging does not create or publish a tag or GitHub release.

Failed uploads leave a draft that a rerun can finish. Published assets are preserved on reruns; use a new tag for changed binaries.

## Licensing

Original contributions use the project's [MIT license](LICENSE). Retain dependency notices and registry provenance. The imported file-format registry is excluded from MIT and still has unresolved reuse permissions; see [third-party notices](THIRD_PARTY_NOTICES.md). Do not describe the entire repository or package as MIT-only.
