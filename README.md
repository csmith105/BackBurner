# BackBurner

BackBurner is a private, LAN-first distributed media-processing system. A central coordinator queues immutable job definitions; capability-aware workers claim jobs only when their host is available; HandBrakeCLI performs the first supported operation. Windows workers can drain gracefully when a human returns, while dedicated Ubuntu workers yield absolutely to the game-development lease system.

The coordinator is deployed as a LAN-only native service on `plex`; the worker fleet and NAS staging workflow are not yet deployed. The repository establishes the contracts, scheduler, retry rules, HandBrake process control, logical NAS paths, and Windows interaction model. No BackBurner worker has written to NASquatch or the Plex libraries.

## Current milestone

- Durable coordinator state stored by one server process in an atomically replaced JSON file.
- Named, editable HandBrake presets whose values are copied into each queued job.
- Read-only coordinator directory scans with unchecked media candidates and atomic batch creation into independently schedulable jobs.
- Capability-aware leasing with UUID lease IDs and monotonically increasing fencing generations.
- Three encoding attempts by default, bounded exponential retry delay, and a visible terminal failure.
- Human or operator interruption returns a job to the queue without consuming its encoding-failure budget.
- HandBrakeCLI progress, ETA, pause (`p`), resume (`r`), and graceful quit (`q`) control.
- Windows idle detection and a notification-area host with Pause and Stop & Requeue actions.
- Personal-desktop, shared-game-worker, and dedicated-render-node availability modes.
- Fleet dashboard with typed worker roles, live state/countdowns, active jobs, and CPU/GPU/upscale capability lanes.
- Explicit Codex inhibit markers, lifecycle-hook templates, and CPU monitoring fallback.
- Full participation in the Cody game-worker broker: short leases, fenced execution, queue preemption, and prompt release.
- Logical path mappings so the same job can resolve SMB paths on Windows and mounted paths on Linux.

## Repository map

- `src/BackBurner.Server`: coordinator API and no-build web UI.
- `src/BackBurner.Contracts`: shared API and persistence contracts.
- `src/BackBurner.Worker.Core`: availability, path safety, coordinator client, and HandBrake execution.
- `src/BackBurner.Worker.Cli`: headless Windows/Linux worker.
- `src/BackBurner.Worker.Windows`: native Windows notification-area worker host.
- `docs/ARCHITECTURE.md`: system shape and invariants.
- `docs/WORKER-CONTRACT.md`: leasing, retries, interruptions, and availability.
- `docs/OPERATIONS.md`: local build/run guidance and eventual deployment shape.
- `docs/DEPLOYMENTS.md`: current installed versions, verification, and rollback coordinates.
- `docs/CODEX-INTEGRATION.md`: reliable exclusion for active agent workloads.
- `docs/DECISIONS.md`: durable decision log.
- `AGENTS.md`: orientation and guardrails for future Codex agents.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet build BackBurner.slnx
```

Run the coordinator locally:

```powershell
dotnet run --project src/BackBurner.Server
```

The development server listens on the URL printed by ASP.NET Core. Its dashboard is `/`; its API is `/api`. See `docs/OPERATIONS.md` before binding it to the LAN.

Run a worker after copying `config/worker.example.json` to an untracked `worker.local.json` and adapting the paths:

```powershell
dotnet run --project src/BackBurner.Worker.Cli -- worker.local.json
```

For deployed workers, prefer supplying the coordinator credential through the
`BACKBURNER_WORKER_API_KEY` process environment instead of putting it in the
JSON file.

BackBurner does not scan, rename, move, or encode anything merely by building or starting the coordinator. A worker must be deliberately configured and a job must be submitted.
