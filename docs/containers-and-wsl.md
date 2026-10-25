# Containers and WSL storage

Open **Extensions → Containers & WSL → Open workflow**. The Docker tab inspects the local Windows Docker engine only when you choose **Inspect local Docker**. It lists containers, images, build-cache records and volumes, with search and observed sizes. No commands run when the view opens, during a drive scan, or on a timer.

## Review and cleanup

| Storage | What is shown | Cleanup option |
| --- | --- | --- |
| Agent/development containers | Name, state and writable-layer bytes when reported. Recognized dev-container labels are a context hint, not proof that a container is an agent or abandoned. Writable-layer sizes exclude mounts and some logs. | For an exited or never-started container, copy a command for its exact full ID. Preserve untracked source, agent results and files inside the container first. Running, paused and unknown states have no removal command. |
| Images | Tags, Docker's container-reference count and image size, including shared layers. Unknown usage remains unknown. | Copy an exact image-ID removal command only when Docker reports zero referencing containers. Keep local-only and offline images. Docker may refuse multiple tags or new references; the command has no force flag. |
| Build cache | Individual default-engine cache records, observed size and in-use status. Shared record/image sizes overlap. | For a record reported not in use, copy a **builder-wide** command: `docker --host npipe:////./pipe/docker_engine builder prune --filter until=168h`. It requests dangling cache older than seven days and retains Docker's confirmation prompt. It does not target just the selected record. Separate Buildx builders require their own `docker buildx du --builder <name>` review and are outside this report. |
| Volumes | Reported usage/reference count, with unsupported or negative values shown as unknown. | Kept, including unattached volumes. No volume-prune, force-removal or broad system-prune command is offered. Preserve databases, models, credentials and results. |

Commands are copied for you to review and run in a terminal; Space Maid never executes cleanup. Docker removal is permanent, outside the Recycle Bin. All copied commands explicitly target the same local named pipe inspected by the view, rather than an ambient remote context. Data is a point-in-time report: re-inspect before acting. Docker's own non-forced operations recheck applicable state, but cannot establish that data is no longer valuable.

The adapter makes a single bounded GET to `/v1.47/system/df` over `\\.\pipe\docker_engine`. It does not use TCP, read Docker configuration or credentials, follow redirects, launch the CLI, or change Docker/WSL state. It needs an engine supporting API 1.47. A request times out after 20 seconds, accepts at most 8 MiB and 5,000 objects, and reports failure rather than publishing a truncated inventory. Refresh/cancellation/failure clears old cleanup commands. Closing the workflow cancels pending work. The API's shared layer sizes are never summed into a reclaimable total.

## Backing disks

Opportunities adds **Containers & virtual disks**. It recognizes folders containing `.vhdx`/`.vhd` files, including relocated imports, and corroborated engine stores with `containers`, `image`, `volumes` and `overlay2`/`windowsfilter` directories. `ext4.vhdx` suggests Linux/WSL; Docker-specific names and folder context suggest Docker. These are filename/layout findings, not registration or content verification. A generic VM disk is not claimed to be a WSL distribution. Folders can contain multiple disks and unrelated files, so their displayed size is the folder total.

Backing-disk and engine-store findings are kept, with disabled cleanup selection and the existing host restrictions against recycling selected ancestors. The detector uses scan metadata only; it never mounts a disk or traverses guest files. A generic build/dependency/environment marker cannot override a backing disk in that folder. Docker objects live inside the virtual disk: do not add their sizes to the host folder total.

The **WSL & virtual disks** tab provides read-only commands for listing distributions and looking up registered `BasePath` values, including custom imports. Use those paths to find the correct disk; Docker Desktop can manage its own disk without a separate `docker-desktop-data` registration. Its current location is available in Settings → Resources → Advanced. Inside the selected distribution, `df -h /` shows Linux usage; it does not measure the disk file's Windows allocation.

To reclaim space, preserve needed work and clean inside the guest/engine first. Compare host **size on disk** with file length, especially for sparse disks. Dynamically expanding disks can retain allocation after guest deletion; size alone does not prove waste or predict compaction savings. For Docker-managed disks, prefer Docker Desktop's supported storage controls. For a WSL disk, back up the distribution, stop agents and services, quit Docker Desktop if it depends on WSL, and only then shut down WSL and follow the applicable Microsoft procedure. `wsl --shutdown` interrupts every WSL distribution and dependent container. `compact vdisk` requires a dynamic disk that is detached or attached read-only. Sparse/encrypted/compressed or unsupported disk types need separate verification; no universal compaction script is offered. Never delete a backing file, unregister a distribution or reset Docker to shrink storage.

Space Maid does not measure guest free blocks, track per-disk growth over time, alter sparse settings, stop containers/distributions, mount/compact disks, or guarantee physical-space reclamation. Nondefault/remote Docker endpoints and separate Buildx builders are not queried.

## Primary sources

Checked 2026-10-25:

- [Docker pruning semantics](https://docs.docker.com/engine/manage-resources/pruning/) and [builder prune filters/confirmation](https://docs.docker.com/reference/cli/docker/builder/prune/).
- [Docker Engine API 1.47 schema](https://github.com/moby/moby/blob/v27.5.1/api/swagger.yaml), including unavailable sizes/reference counts and shared layers.
- [Docker Windows named-pipe endpoint](https://docs.docker.com/desktop/troubleshoot-and-support/faqs/general/) and [CLI endpoint/context precedence](https://github.com/docker/cli/blob/master/cli/command/cli.go).
- [Docker Desktop WSL storage and relocation](https://docs.docker.com/desktop/features/wsl/).
- [Microsoft WSL disk-space management](https://learn.microsoft.com/en-us/windows/wsl/disk-space) and [DiskPart compaction prerequisites](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact-vdisk).

## Verification

Release build and Windows x64 publish passed. `Verify-Package.ps1` verified all 26 required assets, including this extension and the isolated worker runtime. The Windows suite and affected rerun passed 438 distinct tests with three administrator-only skips; the final 20-test rerun covered Docker parsing, bounded read-only requests, stalled response-body cancellation, backing-disk/ancestor protection, and the complete WPF workflow. Real manifest loading, object selection, filters, disabled volume actions, builder-wide advice, failure/cancellation recovery and normal/compact light/dark rendering were exercised with owned fixtures. The independent review's deadline finding was fixed. No local Docker engine pipe was available: real engine data, cleanup, guest accounting and disk compaction/reclamation remain unverified.
