# Roadmap

## M0: executable skeleton

- Coordinator queue, presets, leases, retry accounting, worker registry, web UI.
- Cross-platform worker, HandBrake control, safe path mapping, partial publication.
- Windows idle prompt and Cody game-worker exclusion.
- Automated state-machine and path-safety tests.

## M1: lab validation

- Install HandBrakeCLI on one Windows desktop and run synthetic encodes into a temporary share.
- Confirm progress/ETA parsing against the installed HandBrake version.
- Validate pause/resume, Stop & Requeue, three-failure terminal behavior, power loss, and stale lease fencing.
- Inventory real CPU, GPU, memory, encoders, and throughput for every worker.
- Install and exercise Codex lifecycle hooks on both personal Windows desktops; verify crash-expiry and concurrent-session behavior.

## M2: first household deployment

- Deploy the coordinator on `plex` and workers as managed services/login applications.
- Establish the NAS staging share and least-privilege accounts.
- Add server-side NAS browsing, richer preset editing/import/export, logs, and job history UI.
- Publish to Movies or Series with collision checks and optional Plex library refresh.

## M3: workflow graph and upscaling

- Evaluate open-source video upscalers using representative clips and objective/visual comparisons.
- Add GPU/VRAM-aware operation capabilities and model storage/versioning.
- Support multi-output 4K and 1080p workflows with shared intermediates.
- Add verification via ffprobe/media hashes before publication.
