# File access

`SpaceMaid.Core.IO.DiskAccess` is the runtime boundary for file access. Use it for file/directory probes, content streams, enumeration, settings/cache writes, moves, deletes, drive information and native file handles. The host's `FileOperations` composes it with confirmation, inventory caching, Shell recycling and sync-provider workflows. Extensions receive the same configured instance as `IExtensionHost.Files`, including the host's current NTFS helper. Use that service for extension file access; keep inventory references and cached measurements on `host.Inventory`.

```csharp
var disk = DiskAccess.Default;
var size = await disk.MeasureFolderAsync(folder, token);
await foreach (var batch in disk.ReadMetadataAsync(folder, token))
    Process(batch);
using var input = disk.OpenRead(settingsPath);
```

An extension uses `host.Files` in place of `DiskAccess.Default` above and passes it to its storage/discovery services. Existing API v1 host implementations remain compatible through the interface's default property; production hosts override it with their configured service. Native safety providers still acquire handles through `DiskAccess` and retain their own validation. Third-party engines keep ownership of their internal I/O.

Backend selection follows the operation:

| Operation | Method |
| --- | --- |
| Whole local NTFS volume inventory | NTFS layout query, falling back to Windows directory metadata and then logical-size enumeration where identities/allocation are unsupported |
| Folder scan or stream-size measurement | Fresh directory membership and identities, scoped NTFS stream metadata when available, otherwise attribute-only Windows stream metadata |
| Recheck or cleanup review | The same scoped reader, with journal reuse only when its existing identity and continuity checks pass |
| Name/attribute discovery | Bounded directory metadata batches; no content reads or drive-sized NTFS inventory |
| Content, settings, cache files and namespace changes | Ordinary filesystem APIs with the requested sharing, creation and overwrite semantics |
| Recycling, online-only state, compression, block sharing and virtual-disk maintenance | The existing specialized Shell/provider/native implementations, retaining their consent, identity, activity and scope checks |

NTFS eligibility depends on the target volume and successful queries, not an administrator-role guess. The host supplies its current authorized helper when available; native access is otherwise attempted without prompting. Unsupported/denied metadata queries fall back within that read operation. No permanent capability cache prevents later helper reconnection or a new request from retrying.

The optional `DiskAccess` source factory is resolved per request. A supplied factory returning null explicitly selects standard access; omitting the factory enables native attempts. Production host requests use the helper when connected and native access otherwise. One source is serialized across requests and `DiskAccess` instances, covering each callback's borrowed buffers. Independent directory reads remain parallel. Cancellation while waiting for that source propagates and releases no other request's lock.

Keep native safety providers below this boundary: open handles through `OpenHandle`, read/write handle metadata through `ReadHandleInformation`/`WriteHandleInformation`, and retain their live identity and path checks. An identity failure, changed namespace, cancelled read or failed mutation must never trigger a retry with weaker permissions or a replacement path. NTFS acceleration supplies metadata; it does not authorize changes. Provider-specific control codes and external tools remain with the providers that validate their contracts. Third-party engines such as SQLite and the model runtime own their internal I/O.

When adding a runtime caller, do not open paths with `File`, `Directory`, `FileStream`, `FileInfo`, `DirectoryInfo`, `RandomAccess` or new native file imports outside `Core/IO`. Use `EnumerateEntries` for streamed directory metadata, the handle methods for native metadata/timestamps, `VolumeInformation`/`ClusterBytes` for volume capabilities, and `DeviceControl` for provider-owned control requests. Pass an already opened stream to serializers and decoders. Native controls retain their provider's policy; the gateway never retries a mutation with weaker flags. Tests and acquisition benchmarks may exercise the backends they verify directly.

The migration includes agent history/evidence and recycle callbacks, media discovery and later revision checks, WSL helper validation and virtual-disk pins, host tool brokers, managed worktree scanning, and the normal CLI benchmark. These paths propagate their configured service. Standalone compatibility entrypoints delegate to an injected implementation with `DiskAccess.Default`; host-aware execution must not choose that fallback itself.

[AGENTS.md](../AGENTS.md) requires this boundary for future agent changes. `./tools/Verify-DiskAccess.ps1` audits production C# sources and managed tooling, including new untracked files, for common managed/native bypasses and hardcoded extension defaults. CI runs the audit before building. The check is deliberately lightweight; reviewers must also inspect indirect path-opening APIs and service propagation. IPC, specialized provider/security calls and third-party engines remain the explicit implementation boundaries described above.

`DiskAccessTests` cover backend changes, fallback accounting, whole-volume unsupported-query routing without scanning the drive, sharing/create semantics, source serialization and cancelled waiters. Existing scoped scanner, inventory, cleanup, storage and installer tests cover the composed workflows. Build success alone does not prove real Shell deletion, provider dehydration, disk compaction or raw-volume access on every filesystem.
