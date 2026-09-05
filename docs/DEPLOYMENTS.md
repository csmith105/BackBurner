# Deployments

This file records non-secret deployment coordinates so a future operator or
Codex task can identify what is live without rediscovering the host. Never put
API keys, login credentials, NAS credentials, Plex tokens, or real media names
here.

## Coordinator on `plex`

- Status: active and enabled on 2026-09-05
- LAN URL: `http://plex:5080`
- Service: `backburner-coordinator.service`
- Release: `20260905-395effd`
- Active link: `/opt/backburner/current`
- Release directory: `/opt/backburner/releases/20260905-395effd`
- Previous release retained for rollback: `/opt/backburner/releases/20260905-2b3b634`
- State: `/var/lib/backburner/backburner-state.json`
- Pre-upgrade state backup: `/var/lib/backburner/backburner-state.json.pre-20260905-395effd`
- Secrets: `/etc/backburner/server.env`, root-owned mode `0600`
- Pre-upgrade environment backup: `/etc/backburner/server.env.pre-20260905-395effd`
- Runtime: self-contained .NET 10 `linux-x64`; no server SDK or shared runtime
  installation

Deployment verification:

- The BackBurner service was active, enabled, running as user and group
  `backburner`, and had zero restarts.
- `/api/health` returned `ok` locally and across the LAN.
- `/api/config` reports authentication disabled, and unauthenticated admin and
  worker API requests succeed on the household LAN as explicitly configured.
- The browser dashboard returned HTTP 200 across the LAN.
- The deployed dashboard contains the read-only directory scanner, atomic batch
  submission UI, and Monokai Classic palette.
- The fleet dashboard shows typed worker roles, human/agent/cooldown/working
  states, live readiness countdowns, active job progress, and CPU encoding, GPU
  encoding, and AI-upscaling capacity lanes.
- The coordinator account can read and traverse the configured `nas-media`
  source root. The underlying NAS media mount remains read-only.
- Plex remained active, returned the same claimed server identity and version,
  and had zero restarts during deployment.
- The existing NAS media mount remained read-only.
- No Plex unit, application-data path, account, network setting, or NAS mount was
  changed by the deployment.

The first development worker, `cody-pc-personal`, is running as the interactive
Windows notification-area host with its `PersonalDesktop` role and host hardware
profile. It intentionally reports `Misconfigured` because `HandBrakeCLI` is not
installed on that machine yet, so it cannot claim a job. No job or batch was
queued and no real media was scanned or changed during the upgrade.

### Rollback

To roll back this release, stop the coordinator, repoint
`/opt/backburner/current` to
`/opt/backburner/releases/20260905-2b3b634`, restore
`/etc/backburner/server.env.pre-20260905-395effd` if the prior authenticated
behavior is desired, and start the coordinator again. Verify health and state
access before removing any newer release. Do not
roll back or delete workflow state unless the release's documented schema
procedure explicitly requires it. To disable BackBurner without affecting
Plex, stop and disable only `backburner-coordinator.service`; preserve
`/var/lib/backburner` and `/etc/backburner`.
