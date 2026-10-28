# NTFS scan performance

## Implemented improvements

`ScanBuilder` now inserts each new file identity with one hash-table lookup. Duplicate identities still reuse the first record, preserve the complete identity (including generation/high bits), and contribute allocation once across hard-link names.

Temporary parsers used by scoped and journal-assisted refreshes allocate the name arena only when a name arrives. Real scoped queries request streams and extra metadata without names, so these parsers previously allocated and discarded a 128 KiB character array for every returned batch. The validated layout count now also reserves file-list and identity-index capacity on an empty builder. Reservation happens only before its first records; reserving the dictionary for every subsequent batch would cause repeated whole-inventory rehashing.

Whole-volume acquisition now defaults to **8 MiB with read-ahead**, with configurable initial buffers between 64 KiB and 64 MiB. Bounded read-ahead uses one serial native producer, two pooled buffer slots and a queue capacity of one. The next query can run while the caller parses or sends the previous block. Callbacks remain ordered and serial on the calling thread; borrowed bytes remain valid until their callback returns. Cancellation and consumer/native failure stop and join the producer before returning storage or disposing the volume handle. Oversized records still grow buffers up to 64 MiB. Scoped requests remain at 1 MiB with serial consumption, avoiding a second buffer and a worker for short 1,024-ID refreshes.

Scan diagnostics now use `Environment.WorkingSet` for current-process memory observations. A focused 30-read probe on this machine measured 346.7 ms for `Process.Refresh()` plus `WorkingSet64`, versus 0.0234 ms for the direct counter. This removes an avoidable observation cost; it is not a whole-scan speed claim. The native benchmark uses the same direct counter.

These changes retain the existing query flags, stream parsing, identity/namespace checks, request-draining behavior and standard-scan fallback. Read-ahead does not issue competing filesystem queries or weaken cancellation publication rules.

## Host allocation replay

Use the pinned SDK, or the existing local SDK under `artifacts/toolchains/dotnet-10.0.403`:

```powershell
dotnet run --project tools/SpaceMaid.Bench -c Release -- --ntfs-replay
```

This deterministic replay prepares one million file records before timing. Whole-volume host mode parses names and two DATA streams, maintains the interactive largest-file ranking, and builds the final snapshot. Scoped mode parses the same file count without names, using a fresh temporary builder per 1,024-record batch. Both modes check record counts and stream-size totals. There is one warmup followed by five samples, with untimed GC between samples. It requires no raw-volume access and reads no personal file contents.

On 28 October 2026, comparing the original builder/parser at `d48408a` with this change, scoped parsing allocated **368,783,176 bytes before and 95,414,800 bytes after: 74.1% less allocation traffic**. This value was identical across the five samples in each run. Whole-volume host allocation was effectively unchanged, about 570 MB. These are allocated bytes, not retained heap or peak working set. Raw samples and the original/modified benchmark binaries are in the ignored `artifacts/ntfs-performance/` directory for this checkout.

Wall-clock samples varied substantially under concurrent machine activity. They do not establish a reliable elapsed-time improvement. Replay excludes filesystem queries, helper IPC, namespace validation, opportunity detection and WPF presentation; it is not a whole-drive scan benchmark.

## Native acquisition benchmark

From an administrator terminal:

```powershell
# Six whole-volume cases, then reverse their order on the second pass.
dotnet run --project tools/SpaceMaid.Bench -c Release -- --ntfs-native 'G:\' --passes 2 --output "$env:TEMP\SpaceMaid-ntfs-native.json"
# One configuration, suitable for a fresh worker process per sample.
dotnet run --project tools/SpaceMaid.Bench -c Release -- --ntfs-native 'G:\' --buffer-mib 4 --read-ahead on --output "$env:TEMP\SpaceMaid-ntfs-4-on.json"
# Bounded access/cancellation probe; different buffer sizes read unequal work.
dotnet run --project tools/SpaceMaid.Bench -c Release -- --ntfs-native 'G:\' --buffer-mib 4 --read-ahead on --probe
```

Write live results to an owned location on a different volume. Updating an output file on the scanned volume changes later accounting fingerprints. The benchmark never falls back to a directory scan: unavailable native access is an explicit failed sample. A non-elevated capability probe on this machine returned Win32 error 5; the elevated 4 MiB read-ahead probe consumed its first 10,331 records and drained cancellation in 4.7475 ms. That first-batch observation is not a worst-case cancellation bound.

Each full sample uses the production metadata reader and flags, parses every returned batch and totals file/name counts, DATA bytes and identity/metadata fingerprints. Memory stays bounded to transient batches rather than building a whole inventory. Query and consumer time **overlap** under read-ahead and must not be added. The benchmark excludes final directory aggregation, helper IPC, opportunity detection and WPF. Fingerprints detect changed work but do not establish an atomic filesystem snapshot; cache and external activity remain uncontrolled. The process-lifetime peak is distinct from the periodically sampled working set.

An exploratory matrix showed substantial timing variation and was stopped after eight complete samples. Its output was on G: and its memory sampling used the slower `Process` counter. Those observations are retained as diagnostic evidence in `artifacts/ntfs-performance/native-matrix.json`, not used as a controlled before/after comparison. The final comparison uses direct memory observations, output on C:, fresh worker processes, and reversed configuration order.

Final results on 28 October 2026, with about 3.825 million returned records per full G: acquisition:

| Configuration | Elapsed samples | Mean elapsed | Data batches | Fresh-worker peak working set |
| --- | --- | --- | --- | --- |
| 1 MiB, serial | 42.26 / 31.64 s | 36.95 s | 1,530 | 40.8–41.1 MiB |
| 4 MiB, serial | 53.76 / 31.51 s | 42.64 s | 383 | 55.1–66.9 MiB |
| 4 MiB, read-ahead | 32.62 / 40.15 s | 36.38 s | 383 | 83.2–88.4 MiB |
| **8 MiB, read-ahead** | **24.55 / 25.61 s** | **25.08 s** | **192** | **126.7–138.4 MiB** |

The selected configuration had **32.1% lower mean elapsed time** than the 1 MiB serial reference in this run, with 87.5% fewer data batches. Both 8 MiB samples were faster than both reference samples. The 4 MiB results did not establish a reliable elapsed improvement. This is observational evidence on this machine: record counts varied from 3,825,148 to 3,825,244 (96 records), later metadata fingerprints differed, and external activity/cache state were uncontrolled. It is not a guaranteed speedup or an exact filesystem snapshot comparison.

The trade-off is higher transient memory and longer individual queries. The native producer owns 16 MiB of initial pooled storage; benchmark peaks additionally include parsing arenas, CLR and pooled allocations and do not predict whole-inventory app peaks. First block delivery was 90–197 ms for 8 MiB read-ahead versus 26–35 ms for the serial reference. Separate first-batch probes observed cancellation drain of 194 ms for 8 MiB read-ahead, 49 ms for 4 MiB read-ahead and 35 ms for 1 MiB read-ahead. These are individual observations, not latency guarantees; synchronous native queries must return before cancellation can finish.

Completed comparison receipts, worker logs and native cancellation/helper validation are retained in the ignored `artifacts/ntfs-performance/corrected-native/` and `native-validation/` directories. Changing only buffer sizing/read-ahead does not establish a full app scan/UI improvement; measure those separate stages next.

## Remaining weak points and next designs

The earlier elevated whole-volume measurement in the [validation history](user-guide-and-validation-history.md) is historical, not rerun for this change: 4.19 million objects in 42.25 seconds, including 37.45 seconds of native queries, 3.072 seconds of parsing and 0.981 seconds of aggregation. About 89% of that run was inside Windows queries. Its uncontrolled cache state prevents comparison with today's replay.

| Priority | Weak point and evidence | Next design | Evidence needed before adoption |
| --- | --- | --- | --- |
| 1 | Buffer sizing and read-ahead reduce handoffs and overlap consumption, but the kernel still performs the same metadata work. | Re-measure the query-time share on target hardware. If it remains dominant, investigate a direct MFT source as a separate backend, retaining the documented-layout fallback. | Equal-completeness names, streams and allocation; fixups, extension records/attribute lists, generations, sparse/compressed/resident data and live-volume changes. Use the existing backend as the accounting reference. |
| 2 | Direct acquisition benchmarks exclude helper pipe backpressure, and synchronous native calls can still delay cancellation until they return. | Measure the new defaults through the authenticated helper and observe cancellation during a native call, not only after a parsed batch. Tune buffer size only when the throughput/memory/latency trade-off is demonstrated. | Equal-scope direct/helper timing, host/helper memory, slow consumption, disconnect, failure and next-request reuse. Keep the two-slot bound and ordered borrowed-buffer contract. |
| 3 | Whole-volume construction grows file/name arrays and a full identity dictionary, then copies file/name-link lists into snapshot arrays. The historical peak working set was 2.26 GB. | Profile live heap at acquisition and aggregation first. If the duplicate arrays cause material pressure, prototype bounded pages that can become immutable snapshot storage, with directory-only aggregation scratch space. Keep compact basename storage; avoid a full path string per file. | Peak live/committed bytes and allocation traffic on million-record inventories; compare traversal, sorting, recheck merge and aggregation cost. Retain snapshot immutability and cancellation. This is a representation change, not a default refactor. |
| 4 | Journal-assisted refresh skips unchanged directory walks but still fetches every reached file's current stream metadata, builds per-file dictionaries and reconstructs names. Scoped parsing now has lower allocation but remains an additional materialization step. | Profile refresh separately. Next, consider parsing scoped records directly into the expected-ID table rather than building a temporary namespace-capable builder. Keep duplicate, missing, unexpected-ID and type checks. Reuse compact name slices during import where their snapshot lifetime is explicit. | Scoped/incremental fixture timing and heap measurements, plus changed membership, hard links, replaced identities, ADS, sparse/resident streams, concurrent changes and cancellation. Do not reduce current metadata reads merely because the journal has no new record. |
| 5 | Acquisition-only timing omits opportunity detection and explorer initialization. The app already logs these stages separately. | Use `detection-complete` and `app-scan-complete` timings to identify which stage actually delays usable results. Optimize a demonstrated extension traversal or presentation bottleneck independently. | End-to-end app logs on realistic inventories; distinguish first preliminary results, completed acquisition and usable final results. No UI-performance conclusion follows from replay. |

The filesystem enumerator must remain serial: Microsoft documents restart/continuation state for [file-layout queries](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/fsctl-query-file-layout) and [query inputs](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_query_file_layout_input). Pipelining means overlapping that serial producer with consumption, not issuing competing restart/continuation requests. Retain the flags that include names, stream metadata, resident/unallocated streams and extra information; requesting less information would change accounting completeness.

Journal-only size reuse is unsafe: Windows can coalesce repeated writes while a file remains open. The existing refresh deliberately fetches current sizes even with an apparently unchanged journal. Any future dirty-file cache would need a separately proven way to cover those open writers, plus wrap/reset/gap fallback. See Microsoft's [change-journal behavior](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records).

A direct MFT reader is the larger architectural option if measured query cost remains dominant. Retain the current `INtfsLayoutSource` backend as fallback, and prototype direct records into the compact builder rather than re-encoding already parsed MFT data into Windows layout buffers. A name-only USN enumeration cannot substitute for complete stream/allocation metadata. No direct-MFT backend is included here.

## Verification boundary

The replay checks deterministic accounting. Focused scanner tests additionally cover metadata without names followed by name-arena growth, duplicate identities, generation/high-bit distinctions, aliases, malformed layout records, named streams, sparse files, preliminary results and cancellation. Scoped and incremental refresh tests exercise their existing identity and namespace guards. The acquisition-engine checks cover serial mode, two-slot overlap, stable borrowed bytes, one producer thread, ordered timing callbacks, growth, cancellation, consumer/native failures, malformed lengths and pre-cancellation.

The final isolated baseline plus task-owned sources built in Release with zero warnings/errors. All **88 affected unit tests** passed. Three separately elevated system checks passed without skips: native first-batch stream accounting, helper process reuse after cancellation, and bounded 32-batch native transport through the helper versus direct acquisition. The independent review found no remaining actionable issue. The shared checkout contained concurrent app/installer work, so unrelated changes were excluded from the verification copy and commit.

Native whole-volume timing requires administrator access. These measurements do not establish full-inventory/helper/UI speed or performance across storage devices. No interface behavior was changed, and no physical UI inspection is claimed.
