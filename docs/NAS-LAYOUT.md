# NAS layout

Keep real server names, share names, mount points, and media titles out of this
public repository. Worker configuration maps logical roots to machine-local UNC
paths on Windows or mount points on Linux.

A deployment can use logical roots such as:

- `incoming`: source files awaiting processing;
- `plex-movies`: final movie publication root;
- `plex-series`: final series publication root;
- a broader read-only source root used by coordinator directory scanning.

Existing libraries may have their own naming practices, so BackBurner validates
path safety but does not impose or rewrite media names. The operator supplies
the final relative path. Staging areas such as `Incoming`, `Working`,
`Completed`, and `Failed` are deployment choices, not repository defaults.
