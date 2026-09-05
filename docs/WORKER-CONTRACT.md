# Worker contract

## Job states

```text
queued -> leased -> running -> succeeded
   ^         |         |
   |         |         +-> queued  (encoder failure below max attempts)
   |         +------------> queued  (lease expiration/interruption)
   +----------------------  (human Stop & Requeue; no failure charged)
                         \-> failed (encoder failure reaches max attempts)
```

`paused` and `draining` describe live worker execution/availability; they do not release ownership. A paused process must continue renewing its lease.

## Fencing

Every claim creates a new lease UUID and increments a monotonic per-job generation. Progress, completion, failure, and interruption calls must match the assigned worker, lease UUID, and generation. Renewing time does not change the token. Once requeued, every message from the previous token is rejected.

## Failure and retry rules

- `maxAttempts` defaults to 3 total encoder attempts.
- `failureCount` increments only when the encoder starts and exits unsuccessfully or produces invalid output.
- Below the maximum, the coordinator requeues with bounded exponential delay and records the error.
- At the maximum, the job becomes `failed` and retains its last error for the UI.
- Human Stop & Requeue, planned service shutdown, and ordinary lease expiration increment `interruptionCount`, not `failureCount`.
- A worker that repeatedly loses leases should eventually be quarantined independently of any job's encoding failure budget.

Retries always begin from a clean partial output. HandBrakeCLI cannot safely resume an encode after its process exits; its interactive pause/resume works only while the original process is alive.

## Availability

A personal Windows desktop is claimable only when human-input idle time exceeds the configured threshold, ambient system and Codex CPU remain quiet for the configured window, and no explicit inhibit applies. Defaults are 15 minutes idle, 15 minutes quiet, and a 30-second preflight warning. Human activity while running changes it to `draining`; it does not silently kill the current encode.

An active explicit inhibit is stronger than screen-idle state and forces an immediate safe yield. BackBurner stops the subprocess, deletes only its partial output, and returns the job to the queue without charging an encoding failure. Expired markers do not block work; malformed or unreadable markers block conservatively.

A dedicated render node bypasses desktop and CPU quiet checks. A shared game worker relies on the game-worker lease/queue exclusion instead of human activity. Both modes continue to honor explicit inhibit markers and the local operator pause.

A Cody game-development worker is excluded when either condition is true:

- `/var/lib/cody-worker/lease.json` has `status` other than `idle`.
- `/var/lib/cody-worker/queue.json` contains a nonempty `requests` array, even if the lease status is idle.

BackBurner reads these files but never locks or modifies them. Their writers serialize on `/var/lib/cody-worker/.state.lock` and atomically replace the JSON files, so readers must tolerate a transient read/replace race and conservatively report unavailable on malformed or missing state when the exclusion integration is enabled.

Known node profile received from the game-worker project:

- Fleet ID and hostname: `cody-gd-nc-1`
- Linux machine ID: `05f86ae2e1c94331b00c9c6d2630eae8`
- Ubuntu 26.04.1 LTS x86_64
- Ryzen 9 6900HX, 8 cores / 16 threads
- Radeon 680M, 4 GiB firmware-reserved VRAM
- 24 GB physical DDR5, approximately 18 GiB OS-visible
- 1 TB Crucial P3 Plus

Its first game-engine production lease is currently live. Do not use it for BackBurner until its local worker is installed and the exclusion detector reports availability.

## Capability examples

- `handbrake`
- `encode:x264`
- `encode:x265`
- `encode:nvenc_h265`
- `encode:qsv_h265`
- `encode:vcn_h265`
- future: `upscale:realesrgan`, `gpu:nvidia`, `vram_gib:12`

Discrete numeric requirements will eventually need typed constraints. The first scheduler uses exact string capabilities and configuration-defined tags.
