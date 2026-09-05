# Deployments

This file records non-secret deployment coordinates so a future operator or
Codex task can identify what is live without rediscovering the host. Never put
API keys, login credentials, NAS credentials, Plex tokens, or real media names
here.

## Coordinator on `plex`

- Status: active and enabled on 2026-09-05
- LAN URL: `http://plex:5080`
- Service: `backburner-coordinator.service`
- Release: `20260905-e593518`
- Active link: `/opt/backburner/current`
- Release directory: `/opt/backburner/releases/20260905-e593518`
- Previous release retained for rollback: `/opt/backburner/releases/20260905-c2862b5`
- State: `/var/lib/backburner/backburner-state.json`
- Pre-upgrade state backup: `/var/lib/backburner/backburner-state.json.pre-20260905-e593518`
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
- The deployed web console contains Dashboard, New job, Workers & queue, and
  History tabs; its static asset URLs are versioned to prevent mixed old/new
  browser caches. The masthead intentionally contains only the `BackBurner_`
  wordmark.
- The New job tab contains the read-only directory scanner, atomic batch
  submission UI, and Monokai Classic palette.
- The fleet dashboard shows typed worker roles, human/agent/cooldown/working
  states, live readiness countdowns, active job progress, and CPU encoding, GPU
  encoding, and AI-upscaling capacity lanes.
- Passwordless LAN identities, worker ownership, immutable job attribution,
  output-size accounting, and transition-based worker history are active. The
  initial `Cody` identity owns `cody-pc-personal`.
- The coordinator account can read and traverse the configured `nas-media`
  source root. The underlying NAS media mount remains read-only.
- Plex remained active, returned the same claimed server identity and version,
  and had zero restarts during deployment.
- The existing NAS media mount remained read-only.
- No Plex unit, application-data path, account, network setting, or NAS mount was
  changed by the deployment.

The first development worker, `cody-pc-personal`, is running the matching
updated binary as the interactive
Windows notification-area host with its `PersonalDesktop` role and host hardware
profile. HandBrakeCLI 1.11.2 has been checksum-verified, installed, and reported
successfully by the live worker. Its typed blocking category distinguishes
human, active-agent, cooldown, and ambient-system gates in retained history. It
remains unavailable while human activity or this task's Codex inhibit is present
and becomes eligible only after the configured idle and quiet windows.
No job or batch was queued and no real media was scanned or changed during the
upgrade or worker verification.

### Rollback

To roll back this release, stop the coordinator, repoint
`/opt/backburner/current` to
`/opt/backburner/releases/20260905-c2862b5`, restore
`/etc/backburner/server.env.pre-20260905-395effd` if the prior authenticated
behavior is desired, and start the coordinator again. Verify health and state
access before removing any newer release. Do not
roll back or delete workflow state unless the release's documented schema
procedure explicitly requires it. To disable BackBurner without affecting
Plex, stop and disable only `backburner-coordinator.service`; preserve
`/var/lib/backburner` and `/etc/backburner`.
