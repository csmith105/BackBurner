# Architecture

## System boundary

BackBurner has one authoritative coordinator and any number of disposable workers. NASquatch stores media bytes; the coordinator stores workflow state; workers perform expensive operations. Plex is a publication destination, not a work queue.

```text
Browser
   |
   v
Coordinator on plex (API + UI + durable state)
   |             ^
 claim/lease     | heartbeat/progress/result
   v             |
Windows workers and Ubuntu workers
   |
   v
NASquatch logical roots -> staging / Plex Movies / Plex Series
```

The coordinator never assumes that a worker can perform every operation. Each heartbeat advertises capabilities such as `handbrake`, `encode:x265`, `encode:nvenc_h265`, or a future `upscale:realesrgan`. Each job carries required capabilities derived from its immutable operation snapshot. Scheduling is set inclusion: a worker may claim a job only if it supplies every requirement.

## Coordinator

The coordinator is an ASP.NET Core service with a static, no-build browser UI. In the first milestone it persists a single state document using fsync-friendly temporary-file replacement under one process-wide lock. This is appropriate for one low-volume coordinator and keeps the first deployment understandable. SQLite or PostgreSQL becomes appropriate before multiple coordinator replicas or substantial reporting queries.

Coordinator responsibilities:

- Store reusable presets and copy them into new jobs.
- Enumerate configured source roots read-only and turn an explicitly selected file set into one batch plus independently schedulable child jobs.
- Validate logical paths and reject dangerous HandBrake argument overrides.
- Select an eligible queued job in FIFO order.
- Issue a lease UUID and increment the job's fencing generation.
- Expire abandoned leases and preserve their interruption history.
- Apply bounded encoder-failure retry policy.
- Keep worker status, job progress, ETA, error summaries, and an audit event stream.

HTTP authentication is a deployment policy rather than a scheduler invariant.
It defaults on, but may be explicitly disabled for a coordinator bound only to
a trusted LAN. The public runtime configuration endpoint lets the browser hide
the key prompt in that mode. Authentication must be restored before the port is
reachable from any untrusted network.

## Worker

The cross-platform worker core is intentionally pull-based. LAN firewall rules only need workers to reach the coordinator. A worker sends availability and capabilities, claims work, resolves logical paths through local mappings, writes a partial output, and atomically publishes the final file.

The headless CLI host is suitable for Ubuntu. The Windows host adds notification-area controls and human idle detection. Both use the same `WorkerAgent` and `HandBrakeExecutor`.

Workers have an explicit operating mode:

- `PersonalDesktop` requires sustained human and machine inactivity, honors Codex inhibit markers, watches Codex/system CPU, and warns before claiming work.
- `SharedGameWorker` joins the existing Cody broker only after the development queue is empty. It uses a 60-second fenced lease, launches HandBrake through `cody-workerctl run`, checks the FIFO every worker poll (maximum 30 seconds), and releases promptly when development work appears.
- `DedicatedRenderNode` bypasses human-idle and ambient-CPU gates, but still honors explicit operator and inhibit controls.

Every heartbeat carries that typed mode, structured availability and activity states, an optional earliest-ready timestamp, capabilities, hardware profile, and active job ID. The dashboard uses the typed fields rather than parsing human-readable status messages. A personal desktop can therefore distinguish current human input from an idle cooldown and show a live countdown; a shared game worker reports a broker reservation as `GameWorkerReserved`; and an active job is joined to the worker record for its name, progress, and ETA.

The dashboard groups queued work and registered workers into CPU encoding, hardware/GPU encoding, and AI-upscaling lanes by capability tags. A worker may appear in multiple lanes because eligibility is set-based. This is not a promise of concurrent execution: the current worker agent has one claim slot and runs at most one BackBurner job at a time. Multi-resource concurrency requires measured CPU, GPU, decoder, memory, thermal, and NAS-I/O budgets before the coordinator gains resource-counted claims.

## Human return and drain behavior

On a human-interactable Windows host, BackBurner may claim only after the configured input-idle threshold. If input resumes during an encode:

1. The worker immediately enters `draining` and stops claiming more work.
2. A lower-right notification shows the active file and best-known ETA.
3. With no action, the current file finishes and the worker becomes unavailable.
4. Pause sends HandBrakeCLI its supported interactive `p` command; the process and lease remain alive. It automatically resumes with `r` when the host is idle again.
5. Stop & Requeue sends `q`, escalates to process-tree termination after a grace period, deletes the partial output, and reports an interruption. It does not consume an encoding attempt.

Before the first encode on a personal desktop, a configurable preflight notification gives the user a final opportunity to pause new jobs. The current defaults require 15 minutes of input idle, 15 minutes of quiet host activity, and a 30-second preflight.

## Higher-priority host work

Explicit, expiring JSON inhibit markers are the authoritative local signal that Codex or another higher-priority workload is active. Each task owns one file so concurrent sessions cannot accidentally release one another. Codex lifecycle hooks create/renew/remove these markers; a process CPU sampler is a conservative fallback when hooks are absent or misconfigured. See `CODEX-INTEGRATION.md`.

On a shared Cody node, BackBurner's coordinator lease and the physical-node broker lease are separate fences. The worker must hold both before launching HandBrake. Losing either fence stops publication and returns the coordinator job without charging an encoder failure.

## Paths and NAS safety

Jobs use logical paths such as `incoming:/source.mkv`, `plex-movies:/Movie Name (2026)/Movie Name (2026).mkv`, and `plex-series:/Show Name (2024)/Season 01/Show Name (2024) - S01E01.mkv`. Worker-local configuration maps each logical root to an SMB UNC path on Windows or a mounted directory on Linux.

Resolution rejects rooted relative portions, `..` traversal, and paths that escape the configured root. The final output must not already exist. HandBrake writes to `<destination>.backburner-partial`; success renames it to the requested destination on the same filesystem. Publishing to Plex is therefore an explicit final step, not an incidental side effect of encoding.

Directory browsing is deliberately asymmetric. The coordinator may receive a read-only mapping for a logical source root so the web UI can scan a directory. A scan uses a bounded video-extension allow-list, skips symbolic links/reparse points, rejects traversal, and returns candidates unchecked. It never queues or modifies anything. Batch submission revalidates that every selected source remains beneath the scanned logical directory and persists the batch plus all child jobs atomically. Every child carries the batch ID but otherwise leases, retries, interrupts, and completes independently.

## Future operation graph

The data model begins with one HandBrake operation, but a job should eventually contain a directed sequence such as probe -> upscale -> encode 4K -> encode 1080p -> verify -> publish. Intermediate artifacts remain distinct from final Plex paths. Upscaling backends will be plugins/capabilities rather than a hard dependency, allowing Real-ESRGAN, Video2X, or later models to coexist and to target only suitable GPUs.
