# Contributing

Keep changes focused. Include screenshots for interface changes and describe how you checked the affected behavior. Use disposable fixtures for cleanup tests; never test deletion on personal files.

## Build and test

Use the .NET 10 SDK pinned in [global.json](global.json). On Windows:

```powershell
dotnet build SpaceMaid.slnx -c Release -m:1
dotnet test tests/SpaceMaid.Tests -c Release -m:1
```

Run desktop tests as a normal user. Two raw-volume checks require administrator access and are skipped otherwise. A successful build does not establish native cleanup, cloud-provider or desktop interaction behavior.

Git must be available on `PATH` for worktree fixtures. For the real Photos metadata checks, install the complete ExifTool Windows bundle as `%LOCALAPPDATA%\Programs\ExifTool\ExifTool.exe`, including its companion files. CI installs its pinned version automatically; local ExifTool-specific tests skip when it is absent.

Linux can build the Windows package but cannot run WPF or Windows integration tests:

```bash
dotnet publish src/SpaceMaid.App/SpaceMaid.App.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --output artifacts/package -m:1
```

[CI](.github/workflows/ci.yml) runs on pull requests and pushes to `main`: Linux builds and verifies the package, then Windows runs the same build's tests. Test results and desktop diagnostics are retained as workflow artifacts.

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

Use `v1.2.3-beta.1` for a prerelease. Tags require three version numbers, each at most 65534, and may include a SemVer prerelease suffix. The [release workflow](.github/workflows/release.yml) stamps the version and repository, runs CI, then uses the pinned Velopack tool to prepare update packages. A self-contained Setup.exe embeds the native installer and portable ZIP for its two installation choices; the ZIP, update feed and checksums are also published using GitHub's built-in token. Both release types use the `win` update channel; stable installations ignore prereleases.

Keep every generated update package and feed asset on the release. The app discovers its repository from the build's `GitHubRepository` property; no owner name or access token is embedded. Releases must be public for unauthenticated update checks. Older plain ZIP builds need one manual install or extraction of a new release to enable updates.

Failed uploads leave a draft that a rerun can finish. Published assets are preserved on reruns; use a new tag for changed binaries.

## Licensing

Original contributions use the project's [MIT license](LICENSE). Retain dependency notices and registry provenance. The imported file-format registry is excluded from MIT and still has unresolved reuse permissions; see [third-party notices](THIRD_PARTY_NOTICES.md). Do not describe the entire repository or package as MIT-only.
