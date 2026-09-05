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
- Validate logical paths and reject dangerous HandBrake argument overrides.
- Select an eligible queued job in FIFO order.
- Issue a lease UUID and increment the job's fencing generation.
- Expire abandoned leases and preserve their interruption history.
- Apply bounded encoder-failure retry policy.
- Keep worker status, job progress, ETA, error summaries, and an audit event stream.

## Worker

The cross-platform worker core is intentionally pull-based. LAN firewall rules only need workers to reach the coordinator. A worker sends availability and capabilities, claims work, resolves logical paths through local mappings, writes a partial output, and atomically publishes the final file.

The headless CLI host is suitable for Ubuntu. The Windows host adds notification-area controls and human idle detection. Both use the same `WorkerAgent` and `HandBrakeExecutor`.

Workers have an explicit operating mode:

- `PersonalDesktop` requires sustained human and machine inactivity, honors Codex inhibit markers, watches Codex/system CPU, and warns before claiming work.
- `SharedGameWorker` yields to the Cody game-worker lease or any queued game request. Once that external exclusion is clear, it can claim immediately.
- `DedicatedRenderNode` bypasses human-idle and ambient-CPU gates, but still honors explicit operator and inhibit controls.

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

## Paths and NAS safety

Jobs use logical paths such as `incoming:/source.mkv`, `plex-movies:/Movie Name (2026)/Movie Name (2026).mkv`, and `plex-series:/Show Name (2024)/Season 01/Show Name (2024) - S01E01.mkv`. Worker-local configuration maps each logical root to an SMB UNC path on Windows or a mounted directory on Linux.

Resolution rejects rooted relative portions, `..` traversal, and paths that escape the configured root. The final output must not already exist. HandBrake writes to `<destination>.backburner-partial`; success renames it to the requested destination on the same filesystem. Publishing to Plex is therefore an explicit final step, not an incidental side effect of encoding.

## Future operation graph

The data model begins with one HandBrake operation, but a job should eventually contain a directed sequence such as probe -> upscale -> encode 4K -> encode 1080p -> verify -> publish. Intermediate artifacts remain distinct from final Plex paths. Upscaling backends will be plugins/capabilities rather than a hard dependency, allowing Real-ESRGAN, Video2X, or later models to coexist and to target only suitable GPUs.
