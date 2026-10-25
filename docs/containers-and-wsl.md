# In-app Docker, WSL and model cleanup

Open **Extensions → Containers & WSL → Open workflow** for Docker or disk compaction. Open **Extensions → AI models → Open workflow** for local Ollama. Hugging Face and LM Studio repositories use **Opportunities → AI models & data → Review cleanup**.

Every action prepares a fresh preview with the exact target and consequences, then requires confirmation. Opening a workflow, scanning and inspecting perform no cleanup. API deletion is permanent; filesystem recycling and direct disk modification are labeled separately.

| Target | Preview and execution |
| --- | --- |
| Stopped Docker container | Full ID, name, engine, writable-layer size and loss of agent/untracked work. Preserve needed data, confirm, then fresh engine/object checks precede non-forced deletion of that ID. Volumes/bind mounts are retained; running, paused and unknown states are kept. |
| Unused Docker image | Full digest/tags, shared-size caveat and offline/rebuild consequences. A fresh zero-reference check precedes deletion without force or parent-image pruning. Conflicts/multiple tags may be refused by Docker. |
| Docker build cache | Exact record ID and rebuild/download consequences. An exact ID filter removes that record after an in-use check; no broad builder/system prune or age-only rule. |
| Unattached local Docker volume | Name, creation/state fingerprint and permanent data-loss warning. Acknowledge preserved data and type its exact name. Fresh checks precede non-forced deletion. External drivers, storage options and unknown state remain protected. |
| Ollama model | Exact name/digest/size and unloaded state, permanent consequences and unknown recovery. Confirmation triggers fresh identity/loaded-alias checks, local API deletion and verification of disappearance. Shared data needed by other models may remain. |
| Hugging Face repository | Whole repository folder with all revisions, snapshots and blobs together. Close model users and keep offline/unavailable downloads. Existing manual-review Recycle Bin flow captures and revalidates the native target; individual blobs are never selected. Restore the whole repository if needed. |
| LM Studio repository | Whole repository with all variants, shards and projectors. Close model users and preserve local-only imports. The existing manual-review Recycle Bin flow keeps companion files together. Arbitrary custom roots without recognized layout are not inferred. |
| WSL/Docker VHDX | Exact file/native identity, file length and unknown expected recovery. Backup acknowledgment gates confirmation. Optional all-WSL shutdown is separate and explicit. A fixed-function UAC helper revalidates the exact target, requires a standalone dynamic detached disk and invokes Windows zero-block compaction. No mounting, deletion or unregistering. |

## Changing state and results

Docker uses its fixed local Windows named pipe and API 1.47; Ollama uses only 127.0.0.1:11434. Neither follows remote contexts, proxy settings or redirects. Requests and body reads are bounded. Incomplete/unknown eligibility fails closed. Lost responses after a mutation produce an **uncertain** result and never an automatic retry.

Pause concurrent Docker/Ollama changes. Their APIs do not offer an atomic compare-and-delete against the captured engine/object state or Ollama digest. Fresh checks reduce stale consent but cannot eliminate a race with retagging, volume replacement or model loading. Non-forced Docker operations also check applicable state. Shared object sizes are not summed into guaranteed physical recovery.

## Virtual disks

The WSL tab lists registered WSL2 ext4.vhdx files and lets you add an exact custom/Docker .vhdx. Registration/filename is context, not inspection of guest contents. File length, allocated size and guest capacity differ; do not add Docker object sizes to the host disk total. Clean guest/engine data first. Zero-block compaction may reclaim nothing if free guest blocks are not zero-filled.

Back up first and close disk users, including Docker Desktop. If selected, wsl --shutdown interrupts every WSL distribution and dependent container. A disk changed during shutdown must be previewed again. Linked/reparse ancestors, hard links, sparse/compressed/encrypted storage, attached disks, fixed disks and differencing chains are refused. The workflow waits once native compaction starts; Cancel/closing cannot roll it back. Lost helper results are uncertain. Before/after file lengths are not claimed as allocation savings.

The elevated helper accepts only the confirmed disk operation through an authenticated current-user/PID pipe. It loads no plugins or supplied shell commands. The scan helper remains read-only. Backing disks and engine stores stay protected from folder recycling. Local .vhd, unsupported model layouts, nondefault/remote engines and separate Buildx builders are outside these actions.

## Sources

Checked 2026-10-25: [Docker API 1.47 schemas and exact cache filters](https://github.com/moby/moby/blob/v27.5.1/api/swagger.yaml), [Ollama deletion](https://docs.ollama.com/api/delete) and [loaded models](https://docs.ollama.com/api/ps), [Microsoft VHDX compaction sample](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Hyper-V/Storage/cpp/compactVirtualDisk.cpp), [CompactVirtualDisk](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-compactvirtualdisk), [WSL disk management](https://learn.microsoft.com/en-us/windows/wsl/disk-space).

## Verification

Release build and Windows x64 publish passed. Package verification covers 32 required assets, including the AI-model extension and fixed-function disk helper. The suite and affected reruns passed 468 distinct Windows tests; three administrator-only checks were skipped. Owned API/metadata fixtures exercise exact targets, anchored cache filters, changed engine/object/digest/loaded state, incomplete reports, uncertain results, bounded requests, compaction identity/protocol checks and full packaged WPF preview/cancel/confirm workflows. Light/dark and compact views were inspected. The desktop regression also switches away during pending compaction and verifies that closing is blocked until completion. Independent review findings were addressed.

These checks do not establish live Docker/Ollama mutation or native VHDX compaction, UAC/shutdown behavior or actual physical reclamation. No personal model, container, volume or virtual disk was deleted or compacted during development.
