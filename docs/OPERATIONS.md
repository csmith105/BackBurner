# Operations

## Status

This repository is safe to build and test locally. It has not installed services, mounted NAS shares, written to NASquatch, changed Plex, or deployed to the Ubuntu Plex server.

## Coordinator development

```powershell
dotnet restore BackBurner.slnx
dotnet run --project src/BackBurner.Server
```

State defaults to `data/backburner-state.json` relative to the server working directory. Set `BackBurner__DataFile` to change it. The server is loopback-only unless its ASP.NET Core URLs are deliberately changed.

Before LAN deployment, set strong independent values for:

- `BackBurner__AdminApiKey`
- `BackBurner__WorkerApiKey`

The prototype web UI sends the admin key from browser session storage. This is acceptable only on a trusted LAN over a controlled origin; add proper user authentication before remote or Internet exposure.

## Worker configuration

Copy `config/worker.example.json` to `worker.local.json`; that filename is ignored by Git. Configure a stable worker ID, the coordinator URL and key, HandBrakeCLI path, exact capabilities, and platform-specific logical roots.

Select the correct `mode`:

- `PersonalDesktop`: human-owned Windows computer; keep the 15-minute idle/quiet defaults initially.
- `SharedGameWorker`: Cody game-development node; configure both game-worker state paths.
- `DedicatedRenderNode`: machine whose sole purpose is rendering/encoding; desktop and ambient-CPU gates are skipped.

Windows mapping example:

```json
{
  "paths": {
    "incoming": "\\\\NASquatch\\Media\\_BackBurner\\Incoming",
    "plex-movies": "\\\\NASquatch\\Media\\Plex Movies",
    "plex-series": "\\\\NASquatch\\Media\\Plex Series"
  }
}
```

Linux should mount the same SMB shares persistently and map them to paths such as `/mnt/nasquatch/media/Plex Movies`. Do not embed SMB credentials in this repository or worker JSON; use the OS credential mechanism and a least-privilege service account.

## Windows interaction

Run `BackBurner.Worker.Windows` in an interactive user session so its notification-area icon and lower-right prompt are visible. It should later be packaged to start at login. The headless CLI is appropriate for validation but cannot present the human-return prompt.

For a personal desktop, configure `%LOCALAPPDATA%\\BackBurner\\inhibits` in `inhibitDirectories`. Install the Codex hook integration described in `CODEX-INTEGRATION.md` on each participating machine. The repository includes the hook template but deliberately does not modify a user's global Codex settings during development.

## Ubuntu shared-node exclusion

For `cody-gd-nc-1`, enable game-worker exclusion with these read-only paths:

```json
{
  "gameWorkerLeaseFile": "/var/lib/cody-worker/lease.json",
  "gameWorkerQueueFile": "/var/lib/cody-worker/queue.json"
}
```

BackBurner must run under a user that can read those two files and access its configured NAS mounts. It must not receive permission to modify the game-worker state.

## Production deployment still required

- Select a dedicated NAS staging layout and service identity.
- Install and verify HandBrakeCLI on each worker.
- Benchmark Cameron's real presets and establish capability tags from actual `HandBrakeCLI --help` output.
- Install the coordinator on `plex`, add TLS/auth appropriate to the LAN, and configure backup of its state.
- Package the Windows host and Linux systemd service.
- Exercise power-loss, coordinator-loss, SMB-loss, and human-return cases using synthetic media before touching the Plex libraries.
