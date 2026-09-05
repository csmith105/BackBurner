# BackBurner

BackBurner is a private, LAN-first distributed media-processing system. A central coordinator queues immutable job definitions; capability-aware workers claim jobs only when their host is available; HandBrakeCLI performs the first supported operation. Windows workers can drain gracefully when a human returns, while dedicated Ubuntu workers yield absolutely to the game-development lease system.

The repository is currently an executable engineering skeleton, not a production deployment. It establishes the contracts, scheduler, retry rules, HandBrake process control, logical NAS paths, and Windows interaction model before any service is installed on Plex or any write is made to NASquatch.

## Current milestone

- Durable coordinator state stored by one server process in an atomically replaced JSON file.
- Named, editable HandBrake presets whose values are copied into each queued job.
- Capability-aware leasing with UUID lease IDs and monotonically increasing fencing generations.
- Three encoding attempts by default, bounded exponential retry delay, and a visible terminal failure.
- Human or operator interruption returns a job to the queue without consuming its encoding-failure budget.
- HandBrakeCLI progress, ETA, pause (`p`), resume (`r`), and graceful quit (`q`) control.
- Windows idle detection and a notification-area host with Pause and Stop & Requeue actions.
- Personal-desktop, shared-game-worker, and dedicated-render-node availability modes.
- Explicit Codex inhibit markers, lifecycle-hook templates, and CPU monitoring fallback.
- Linux exclusion checks compatible with the Cody game-worker lease and queue files.
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

BackBurner does not scan, rename, move, or encode anything merely by building or starting the coordinator. A worker must be deliberately configured and a job must be submitted.
