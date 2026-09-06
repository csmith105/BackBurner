# Operations

## Status

This public document describes supported operation without publishing a private
network inventory. Record live release, host, URL, service, state, verification,
and rollback coordinates in an ignored `DEPLOYMENTS.local.md` based on the
public `DEPLOYMENTS.md` template.

## Coordinator development

```powershell
dotnet restore BackBurner.slnx
dotnet run --project src/BackBurner.Server
```

State defaults to `data/backburner-state.json` relative to the server working directory. Set `BackBurner__DataFile` to change it. The state document includes queue state, presets, local identities, worker ownership, audit events, and transition-based worker history. Completed worker-activity intervals default to a 365-day retention through `BackBurner__HistoryRetentionDays`. Back up the state document as a unit; do not edit it while the coordinator is running. The server is loopback-only unless its ASP.NET Core URLs are deliberately changed.

Authentication defaults on. For an authenticated deployment, set strong independent values for:

- `BackBurner__AdminApiKey`
- `BackBurner__WorkerApiKey`

The prototype web UI sends the admin key from browser session storage. For a
coordinator that is provably reachable only from a trusted household LAN, set
`BackBurner__RequireAuthentication=false`; the browser then hides the key prompt
and workers may leave `workerApiKey` empty. Re-enable authentication before any
reverse proxy, port forwarding, routed guest network, remote access, or Internet
exposure.

The web console lands on **Dashboard** and separates **New job**, **Workers & queue**, and **History** into top navigation tabs. The identity selector is attribution, not authentication: creating or selecting a name writes only the identity UUID to that browser's local storage. Select an identity before queueing so jobs record the operator. Worker cards provide an independent owner selector for grouping machines by person.

The versioned application API is documented in `INTEGRATION-API.md`; its live
OpenAPI document is `/api/v1/openapi.json`. Job creation and the read-only fleet
summary use the admin-key policy. Per-job reads and cancellation use the
one-time control token returned at creation. Back up integration clients' job ID
and token pairs in their private state because the coordinator stores only a
hash and cannot recover a lost token.

An integration-created job requires `protocol:publication-fence-v1`. Upgrade at
least one worker to the matching release before submitting through `/api/v1`.
Older workers remain safe and can continue ordinary dashboard jobs, but the
scheduler deliberately leaves integration work queued for a current worker.

## Worker configuration

Copy `config/worker.example.json` to `worker.local.json`; that filename is ignored by Git. Configure a stable worker ID, the coordinator URL and key, HandBrakeCLI path, exact capabilities, and platform-specific logical roots.

When coordinator authentication is enabled, prefer leaving `workerApiKey` empty
in the local JSON and supplying `BACKBURNER_WORKER_API_KEY` through the process
environment or service manager. In explicit unauthenticated LAN mode, leave both
sources empty. Do not print a real value in startup scripts or commit it.

Select the correct `mode`:

- `PersonalDesktop`: human-owned Windows computer; keep the 15-minute idle/quiet and 30-second recent-input defaults initially. The dashboard shows current human use in yellow, then a live idle-cooldown countdown. Yellow is a normal policy gate; red is reserved for unavailable agent reservations, offline workers, and faults.
- `SharedGameWorker`: Cody game-development node; configure both game-worker state paths.
- `DedicatedRenderNode`: machine whose sole purpose is rendering/encoding; desktop and ambient-CPU gates are skipped.

Windows mapping example (placeholders only):

```json
{
  "paths": {
    "incoming": "\\\\YOUR-NAS\\YOUR-STAGING-SHARE\\Incoming",
    "plex-movies": "\\\\YOUR-NAS\\YOUR-MOVIE-SHARE",
    "plex-series": "\\\\YOUR-NAS\\YOUR-SERIES-SHARE"
  }
}
```

Linux should mount the same SMB shares persistently and map them to private
local mount points. Do not embed SMB credentials in this repository or worker
JSON; use the OS credential mechanism and a least-privilege service account.

## Coordinator source scanning

The directory-batch UI can see only logical source roots explicitly configured under `BackBurner:SourceRoots`. In a systemd environment file, a read-only NAS mapping resembles:

```text
BackBurner__SourceRoots__source-media=/mnt/your-read-only-source
```

The coordinator account needs read and directory-traversal permission, never write permission, on that mount. Each worker that may claim those jobs must map the same `source-media` logical root to its local SMB or mounted representation. A scan recognizes a bounded set of common video extensions and is a candidate listing, not a media probe. Results start unchecked and queue nothing until the operator submits a selection. `BackBurner__MaximumScanFiles` defaults to 5,000 and may be lowered for a particularly large tree.

## Windows interaction

Run `BackBurner.Worker.Windows` in an interactive user session so its notification-area icon and lower-right prompt are visible. The supported per-user installer publishes a self-contained version, stores configuration outside the checkout, starts it, and registers it at login. See `WINDOWS-WORKER-SETUP.md`. The headless CLI is appropriate for validation but cannot present the human-return prompt.

For a personal desktop, configure `%LOCALAPPDATA%\\BackBurner\\inhibits` in `inhibitDirectories`. Install the Codex hook integration described in `CODEX-INTEGRATION.md` on each participating machine. The repository includes the hook template but deliberately does not modify a user's global Codex settings during development.

## Ubuntu shared-node exclusion

For a shared development node, enable game-worker exclusion with the broker's
private read-only state paths:

```json
{
  "mode": "SharedGameWorker",
  "gameWorkerLeaseFile": "/path/to/game-worker/lease.json",
  "gameWorkerQueueFile": "/path/to/game-worker/queue.json",
  "codyWorkerBrokerPath": "/usr/local/bin/cody-workerctl",
  "codyWorkerProfile": "cpu",
  "codyWorkerLeaseTtlSeconds": 60,
  "codyWorkerRenewSeconds": 20
}
```

BackBurner must run as the broker-enabled `cody` user so `cody-workerctl run` can create the fenced user-systemd scope, while also being able to read the two state files and access its configured NAS mounts. BackBurner never edits broker JSON directly. Use profile `gpu` instead of `cpu` only for jobs whose configured worker capability genuinely requires the shared GPU.

Before deployment, exercise this exact sequence with synthetic media: immediate acquisition on an idle/empty node, a 60-second lease renewal, a development request joining during an encode, HandBrake interruption within 30 seconds, coordinator requeue with no failure charged, broker cleanup, and successful acquisition by the waiting development task.

## Production deployment still required

- Select a dedicated NAS staging layout and service identity.
- Install and verify HandBrakeCLI on each worker.
- Benchmark the operator's real presets and establish capability tags from actual `HandBrakeCLI --help` output.
- Benchmark combined CPU encoding and GPU/upscaling workloads before enabling more than one claim slot on any worker.
- Configure and verify backup of the coordinator state; add a reverse proxy and TLS before any access beyond the trusted LAN.
- Package the Windows host and Linux systemd service.
- Exercise power-loss, coordinator-loss, SMB-loss, and human-return cases using synthetic media before touching the Plex libraries.

## Native Ubuntu coordinator layout

The supported coordinator deployment is a self-contained `linux-x64` release managed by `systemd`; it does not require the .NET SDK on the server. Use the unit, environment template, installation checklist, and rollback procedure under `deploy/linux`. Releases live under `/opt/backburner/releases`, the active release is selected by `/opt/backburner/current`, durable workflow state lives under `/var/lib/backburner`, and real API keys live only in the root-readable `/etc/backburner/server.env` file.

The coordinator service is separate from Plex and requires neither Plex application-data access nor NAS media access. Do not add a dependency on `plexmediaserver.service`, alter the Plex unit, or grant the `backburner` account access to Plex state. Port 5080 is the LAN-only default until a reverse proxy and stronger user authentication are deliberately added.
