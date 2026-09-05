# Observed NAS layout

Read-only inspection on 2026-09-04 confirmed `\\NASquatch\Media` is reachable over SMB and contains the active roots:

- `\\NASquatch\Media\Plex Movies`
- `\\NASquatch\Media\Plex Series`

Existing movie directories commonly use `Title (Year)`. Existing series naming varies, so BackBurner will not impose or rewrite names. Cameron supplies the final relative path.

No BackBurner staging directories exist yet and none were created. A future setup pass should agree on a structure resembling `_BackBurner/Incoming`, `_BackBurner/Working`, `_BackBurner/Completed`, and `_BackBurner/Failed`, but those names are proposals only.
