# Ubuntu coordinator deployment

The coordinator runs as an independent native `systemd` service. It does not
modify or depend on the Plex service, and it does not need access to Plex's
application data or the NAS media mount.

## Layout

- `/opt/backburner/releases/<version>`: immutable published application
- `/opt/backburner/current`: symlink to the active release
- `/var/lib/backburner`: coordinator state, owned by `backburner`
- `/etc/backburner/server.env`: root-readable deployment settings and secrets
- `/etc/systemd/system/backburner-coordinator.service`: service definition

Keep at least the previous release directory so rollback is a symlink change.
Back up `/var/lib/backburner/backburner-state.json`; releases do not contain
workflow state.

## Publish

From a development checkout with the .NET 10 SDK:

```powershell
dotnet test BackBurner.slnx -c Release
dotnet publish src/BackBurner.Server -c Release -r linux-x64 --self-contained true -o artifacts/server-linux-x64
```

Transfer the contents of `artifacts/server-linux-x64` into a new immutable
release directory. Do not build or install the SDK on the Plex server.

## First installation

1. Create a locked `backburner` system user with no interactive shell.
2. Create `/opt/backburner/releases`, `/var/lib/backburner`, and
   `/etc/backburner` without changing Plex directories or permissions.
3. Install a new release as root-owned and not group-writable.
4. Point `/opt/backburner/current` at that release.
5. Create `/etc/backburner/server.env` from `server.env.example`, generate two
   independent keys, and set it to `root:root` mode `0600`.
6. Install `backburner-coordinator.service`, run `systemctl daemon-reload`, and
   enable and start only that unit.
7. Verify `/api/health`, an authenticated `/api/admin/snapshot`, state-file
   permissions, journal output, and the pre/post Plex identity response.

The LAN listener defaults to port 5080. Restrict that port to the trusted LAN
at the host or network firewall. A reverse proxy is optional for the LAN-only
milestone and becomes appropriate when adding TLS or remote access.

## Upgrade and rollback

Install every upgrade into a new release directory. Stop the coordinator,
change the `current` symlink, start it, and verify health. To roll back, repeat
those steps with the previous release. Never delete the coordinator state as
part of application rollback. If a future release changes the state schema,
document and back up that state before activating it.
