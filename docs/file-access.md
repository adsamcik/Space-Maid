# File access

`SpaceMaid.Core.IO.DiskAccess` is the runtime boundary for file access. Use it for file/directory probes, content streams, enumeration, settings/cache writes, moves, deletes, drive information and native file handles. The host's `FileOperations` composes it with confirmation, inventory caching, Shell recycling and sync-provider workflows. Extensions use host inventory services for discovery and measurement and `DiskAccess` for their own local storage.

```csharp
var disk = DiskAccess.Default;
var size = await disk.MeasureFolderAsync(folder, token);
await foreach (var batch in disk.ReadMetadataAsync(folder, token))
    Process(batch);
using var input = disk.OpenRead(settingsPath);
```

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

When adding a runtime caller, do not open paths with `File`, `Directory`, `FileStream`, `FileInfo`, `DirectoryInfo`, `RandomAccess` or new `CreateFileW` imports outside `Core/IO`. Pass an already opened stream to serializers and decoders. Filesystem enumeration and control-code implementations in scanner/provider backends are the low-level implementations, not alternate caller APIs. Tests and acquisition benchmarks may exercise those backends directly.

`DiskAccessTests` cover backend changes, fallback accounting, whole-volume unsupported-query routing without scanning the drive, sharing/create semantics, source serialization and cancelled waiters. Existing scoped scanner, inventory, cleanup, storage and installer tests cover the composed workflows. Build success alone does not prove real Shell deletion, provider dehydration, disk compaction or raw-volume access on every filesystem.
