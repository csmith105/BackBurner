# Decision log

## 2026-09-04: Encoding-first vertical slice

Begin with HandBrake encoding, durable queueing, worker eligibility, progress, retry, and safe publication. Do not make Topaz a dependency. Research multiple open-source upscalers after the encoding loop is dependable.

## 2026-09-04: One .NET codebase

Use .NET 10 for the ASP.NET Core coordinator, cross-platform worker core, headless CLI, and native Windows notification-area host. This keeps contracts and process control in one language while permitting Windows-specific user interaction and Linux services.

## 2026-09-04: Finish-current is the human-return default

Human activity moves a Windows worker to draining. The current encode continues unless the user chooses Pause or Stop & Requeue. The worker claims no next job until it becomes idle again.

## 2026-09-04: Three total encoding attempts

Encoder failures retry automatically up to three total attempts. A human cancellation or other orchestration interruption never consumes that budget.

## 2026-09-04: Presets are mutable templates; jobs are immutable snapshots

The UI can load, edit, save, and discard preset values in a PuTTY-like workflow. Queuing copies the current form values into the job, so later preset edits cannot alter work already queued.

## 2026-09-04: Logical paths, explicit publication roots

Coordinator jobs store logical root paths rather than Windows UNC or Linux mount syntax. Movies and series are separate roots. Cameron supplies the final relative directory and filename; BackBurner validates safety but does not attempt to correct Plex naming in the first milestone.

## 2026-09-04: Game development has priority on shared nodes

BackBurner treats the Cody worker lease and FIFO queue as an external absolute exclusion. It reads but never participates in or modifies that protocol. This avoids racing a media process against interactive engine work.

## 2026-09-04: Single-process JSON persistence for the prototype

Use an atomically replaced state document while there is one coordinator and low queue volume. Migrate behind the state-store interface to SQLite/PostgreSQL before multi-instance coordination or richer query/reporting requirements.

## 2026-09-05: Explicit host roles and layered activity detection

Configure every worker as a personal desktop, shared game worker, or dedicated render node. A personal desktop requires 15 minutes of input inactivity plus a quiet-machine window and preflight warning. A dedicated render node bypasses those gates. An expiring, per-session inhibit marker is the authoritative Codex signal; Codex/system CPU monitoring is only a fallback because an open desktop app is not evidence that an agent is executing.

## 2026-09-05: Shared NUCs participate in the existing broker

Do not create a BackBurner-specific lock on Cody game-development nodes. Hold both the coordinator job fence and an immediate 60-second `cody-workerctl` fence, run HandBrake inside the broker's systemd scope, poll the development FIFO no less often than every 30 seconds, and release when interactive work appears. A failed immediate acquisition is canceled instead of retaining FIFO priority.

## 2026-09-05: Native versioned systemd deployment for the coordinator

Run the LAN-only coordinator beside Plex as an independent native Linux service rather than introducing a container runtime solely for BackBurner. Publish a self-contained `linux-x64` application, install immutable versioned releases under `/opt/backburner/releases`, and select the active version through `/opt/backburner/current`. Keep workflow state under `/var/lib/backburner` and deployment secrets under `/etc/backburner`, outside every release. The BackBurner unit must not modify, depend on, restart, or share an identity with the Plex unit. This layout keeps deployment small and makes application rollback a symlink change without rolling back or deleting queue state.
