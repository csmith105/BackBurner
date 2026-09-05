# Deployments

This file records non-secret deployment coordinates so a future operator or
Codex task can identify what is live without rediscovering the host. Never put
API keys, login credentials, NAS credentials, Plex tokens, or real media names
here.

## Coordinator on `plex`

- Status: active and enabled on 2026-09-05
- LAN URL: `http://plex:5080`
- Service: `backburner-coordinator.service`
- Release: `20260905-9e3e55d`
- Active link: `/opt/backburner/current`
- Release directory: `/opt/backburner/releases/20260905-9e3e55d`
- State: `/var/lib/backburner/backburner-state.json`
- Secrets: `/etc/backburner/server.env`, root-owned mode `0600`
- Runtime: self-contained .NET 10 `linux-x64`; no server SDK or shared runtime
  installation

Deployment verification:

- The BackBurner service was active, enabled, running as user and group
  `backburner`, and had zero restarts.
- `/api/health` returned `ok` locally and across the LAN.
- An unauthenticated admin request returned HTTP 401; an authenticated snapshot
  succeeded.
- The browser dashboard returned HTTP 200 across the LAN.
- Plex remained active, returned the same claimed server identity and version,
  and had zero restarts during deployment.
- The existing NAS media mount remained read-only.
- No Plex unit, application-data path, account, network setting, or NAS mount was
  changed by the deployment.

The initial state contains only the built-in starter preset, with no jobs or
workers. Before queuing real media, configure and validate a worker using
synthetic input.

### Rollback

The first deployment has no earlier BackBurner release. To disable it without
affecting Plex, stop and disable only `backburner-coordinator.service`. Preserve
`/var/lib/backburner` and `/etc/backburner`.

After a later upgrade, stop the coordinator, repoint
`/opt/backburner/current` to the prior immutable release, and start the
coordinator again. Verify health and authenticated state before removing any
newer release. Do not roll back or delete workflow state unless the release's
documented schema procedure explicitly requires it.
