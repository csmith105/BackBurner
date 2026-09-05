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

A personal Windows desktop is claimable only when human-input idle time exceeds the configured threshold, ambient system and Codex CPU remain quiet for the configured window, and no explicit inhibit applies. Defaults are 15 minutes idle, 15 minutes quiet, a 30-second recent-input window, and a 30-second preflight warning. The structured `activityState` distinguishes `HumanActive` from `IdleCooldown`; the latter means input has stopped but an idle or quiet-machine timer has not matured. Its heartbeat includes the earliest known `readyAt` time for a dashboard countdown. That time is the next possible transition, not a guarantee if CPU or higher-priority work becomes active. Human activity while running changes the worker to `draining`; it does not silently kill the current encode.

An active explicit inhibit is stronger than screen-idle state and forces an immediate safe yield. BackBurner stops the subprocess, deletes only its partial output, and returns the job to the queue without charging an encoding failure. Expired markers do not block work; malformed or unreadable markers block conservatively.

A dedicated render node bypasses desktop and CPU quiet checks. A shared game worker relies on the game-worker lease/queue exclusion instead of human activity. Both modes continue to honor explicit inhibit markers and the local operator pause.

Every heartbeat identifies the worker as `PersonalDesktop`, `SharedGameWorker`, or `DedicatedRenderNode`. Dashboard labels and policy must use this typed mode rather than infer ownership from a hostname or free-form profile field. An active job ID means the worker's one execution slot is occupied even if it is otherwise healthy.

A Cody game-development worker is excluded when either condition is true:

- `/var/lib/cody-worker/lease.json` has `status` other than `idle`.
- `/var/lib/cody-worker/queue.json` contains a nonempty `requests` array, even if the lease status is idle.

BackBurner reads these files directly only as a fail-closed availability check. It never writes them or takes `.state.lock` itself. To perform work it invokes the existing `/usr/local/bin/cody-workerctl` broker, requests an immediate 60-second background lease with a 60-second queue-entry lifetime, and cancels the request immediately if no lease is granted. It never remains in the FIFO waiting for priority.

With a lease, HandBrake runs through the broker's fenced `run` command inside a lease-tagged systemd scope. BackBurner renews every 10–30 seconds, continues inspecting the development FIFO every worker poll, and safely interrupts and releases as soon as another request appears. The broker's UUID and generation are independent of the coordinator's UUID and generation; both must remain valid.

Known node profile received from the game-worker project:

- Fleet ID and hostname: `cody-gd-nc-1`
- Linux machine ID: `05f86ae2e1c94331b00c9c6d2630eae8`
- Ubuntu 26.04.1 LTS x86_64
- Ryzen 9 6900HX, 8 cores / 16 threads
- Radeon 680M, 4 GiB firmware-reserved VRAM
- 24 GB physical DDR5, approximately 18 GiB OS-visible
- 1 TB Crucial P3 Plus

The worker baseline was reported released and stable at the final integration handoff. BackBurner is still not deployed there; deployment requires installing the local worker and validating broker preemption with synthetic media.

## Capability examples

- `handbrake`
- `encode:x264`
- `encode:x265`
- `encode:nvenc_h265`
- `encode:qsv_h265`
- `encode:vcn_h265`
- future: `upscale:realesrgan`, `gpu:nvidia`, `vram_gib:12`

Discrete numeric requirements will eventually need typed constraints. The first scheduler uses exact string capabilities and configuration-defined tags.

Capability overlap does not create parallel execution slots. A host that advertises both `encode:x265` and `upscale:realesrgan` is eligible for either class, but the current scheduler assigns it only one job at a time. Resource-counted CPU/GPU concurrency remains a benchmark-driven follow-up.
