# Deployment record template

Public source control must describe the deployment shape without identifying a
private network. Keep real hostnames, addresses, machine IDs, share names,
release paths, backup names, API keys, and media names in an ignored
`docs/DEPLOYMENTS.local.md` or a private operations system.

For each coordinator deployment, record privately:

- install date and operator;
- host and LAN URL;
- service name and service account;
- active and prior release identifiers;
- active-release link, state path, secret-store path, and backup path;
- authentication and network-boundary mode;
- health, dashboard, API, persistence, permission, and rollback checks;
- confirmation that unrelated services and production media were unchanged.

For each worker, record privately:

- stable worker ID, owner, host, and operating mode;
- installed release and HandBrake version;
- logical root names (without credentials);
- verified capabilities and hardware profile;
- heartbeat, idle/inhibit, interruption, and synthetic encode checks.

## Rollback invariant

Install releases side by side. Stop only the BackBurner service, select the
previous application release, and restart it. Preserve coordinator state and
secrets unless a release-specific migration procedure explicitly says
otherwise. Verify health and state access before removing any release.
