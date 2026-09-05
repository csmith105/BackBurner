# BackBurner agent guide

Read `README.md`, `docs/ARCHITECTURE.md`, `docs/WORKER-CONTRACT.md`, `docs/DECISIONS.md`, and `docs/OPERATIONS.md` before changing behavior. Record architecture-affecting choices in `docs/DECISIONS.md` in the same commit as the implementation.

## Non-negotiable safety rules

1. Never modify, rename, delete, or reorganize existing content under `\\NASquatch\Media` during development or tests. Use generated temporary directories and synthetic files.
2. Never expose a partial encode at its final Plex filename. Encode to a `.backburner-partial` path and publish by a same-filesystem atomic rename only after HandBrake succeeds.
3. Never overwrite an existing destination. Treat that as a terminal conflict requiring a human decision.
4. Every worker mutation must present both the lease UUID and fencing generation. A stale worker must be rejected even when it still has an old lease ID.
5. An active, recovering, or quarantined Cody game-worker lease is an absolute BackBurner exclusion. An idle node with a nonempty game-worker FIFO queue is also excluded.
6. Encoder failures consume the bounded attempt budget. Human stops, service shutdowns, and coordinator lease interruptions do not.
7. Job creation copies a preset snapshot. Editing or deleting the named preset must never change an already queued job.
8. Do not commit credentials, worker API keys, NAS credentials, Plex tokens, machine-local mappings, or real media filenames.

## Development expectations

- Keep coordinator state transitions deterministic and covered by tests.
- Keep OS-specific behavior behind interfaces in `BackBurner.Worker.Core`.
- Prefer structured argument arrays over shell command strings.
- Treat filesystem paths received from the coordinator as logical paths and resolve them through configured roots; reject traversal.
- Preserve actionable event history for every retry, interruption, lease expiration, and terminal failure.
- Keep documentation detailed enough for a non-programmer to ask a new Codex task to continue the work safely.
