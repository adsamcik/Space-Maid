# Space Maid development

Space Maid is a personal Windows app using the pinned .NET SDK, WPF and bundled extensions. Preserve unrelated work. Complete scoped implementation, appropriate checks, required independent review and authorized local commits/integration before returning. Do not publish, push or deploy without explicit authorization.

## Required file access boundary

- All production C# disk access, including extensions, helpers, installer, isolated worker and managed command-line tools, goes through `SpaceMaid.Core.IO.DiskAccess`. Add a missing operation there before using it elsewhere. Do not create another filesystem facade or choose NTFS versus standard access in a caller.
- Extensions obtain the configured instance from `IExtensionHost.Files` and pass it through their services, discovery, leases, execution and callback revalidation. Host-aware app services use `FileOperations.Files`. Keep transient inventory references and cached-size requests on `host.Inventory`.
- Do not use `DiskAccess.Default` in extension operational methods or host-aware brokers. It is allowed only in explicit standalone/compatibility overloads or constructors, the API v1 default interface property, subprocess/CLI composition roots, and shared Core safety/backend implementations that have no host context. Never use an ambient/global override to switch an extension's service.
- Outside `src/SpaceMaid.Core/IO`, do not call `System.IO.File`, `Directory`, `DriveInfo` or `RandomAccess`, construct path-based `FileStream`, `FileInfo`, `DirectoryInfo` or `FileSystemEnumerable`, or add equivalent aliases/wrappers. Pass streams obtained from `DiskAccess` to serializers, XML readers, image decoders and other stream consumers.
- Ordinary native file open, handle metadata, timestamps, volume information, cluster geometry and device-control primitives belong in `Core/IO`. Use the gateway's operations rather than duplicating kernel32 file imports. Scanner construction belongs inside the gateway and scanner implementation; runtime analysis and refresh callers also enter through `DiskAccess`.
- Shell recycling, Cloud Files, virtual-disk controls and sandbox security remain specialized providers. Their ordinary file operations still go through the gateway. IPC/process/security APIs are not filesystem shortcuts. CLR assembly loading, SQLite and the model engine own their internal I/O; retain validated paths/handles around their calls rather than replacing those libraries.
- Preserve exact access/share/create flags, no-recall behavior, held identities, activity checks, consent and scope. A failed mutation, identity check, changed namespace or cancellation must not trigger a path-based retry or weaker permissions. NTFS fallback applies to eligible metadata reads only. Keep source serialization and cancellation while waiting for a helper.
- Preserve existing callable constructor/method shapes when injecting services: add delegating overloads rather than replacing old signatures with optional parameters. Existing reflection-based extension checks must continue working. Do not make reflected single-name lookups ambiguous.

Tests may use direct filesystem APIs to create and independently verify owned disposable fixtures. They must not become alternate production implementations. Native acquisition/replay benchmarks may directly exercise the backend they measure; the normal scan benchmark uses `DiskAccess`. PowerShell build/test/package bootstrap scripts use their platform's file APIs and must not contain product storage logic.

## Verification and review

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [file access](docs/file-access.md) for current commands and contracts.

Run the boundary audit for disk-access changes and before completing a migration:

```powershell
./tools/Verify-DiskAccess.ps1
```

Resolve findings in the shared API and its callers. Do not expand exceptions merely to make the audit pass. The audit catches common bypasses; review aliases, indirect path-opening APIs and dependency propagation as well.

Build with the repository's pinned toolchain and run focused affected tests:

```powershell
dotnet build SpaceMaid.slnx -c Release -m:1
./tools/Test.ps1 -NoBuild -Filter 'FullyQualifiedName~SpaceMaid.Tests.DiskAccessTests'
```

Use existing Unit/System boundaries. Run native/UI checks as a normal user on owned fixtures when the affected behavior needs them. Desktop tests share one Application and production assembly contexts; extend the existing harness instead of adding competing Applications. Check host-service identity before tests replace hosts/services with doubles, and forward the real service through wrappers that retain host capabilities.

For complex or risky changes, obtain one independent review-only subagent before completion; reviewers must not edit or delegate. Reuse valid results and recheck affected failures rather than restarting unrelated suites. Never equate compilation with live Shell, provider, filesystem or physical-device proof.
