# Codex integration

## Goal

An open Codex desktop window does not make a host unavailable by itself. An actively running agent does. BackBurner therefore combines an explicit lifecycle signal with resource monitoring instead of trying to infer execution from window or process existence.

## Reliable signal: expiring inhibit markers

Each active Codex session owns one JSON file under the worker's configured `inhibitDirectories` path. On Windows the default convention is `%LOCALAPPDATA%\\BackBurner\\inhibits`; on Linux it is `~/.local/state/backburner/inhibits`. `BACKBURNER_INHIBIT_DIR` overrides the helper's default.

The bundled helper is:

```text
.agents/skills/backburner-host-control/scripts/backburner_inhibit.py
```

It supports `acquire`, `release`, `status`, `prune`, and `hook`. Writes use temporary-file replacement. Markers expire after 24 hours by default so a crashed session cannot disable encoding forever. One session never deletes another session's marker.

The repository also contains `integrations/codex/hooks.example.json`. Replace `PATH_TO_REPO` with this repository's absolute path, then merge its hook entries into the host's `~/.codex/hooks.json` (Windows uses the same location under the user profile). Do this on each computer that both runs Codex and participates as a BackBurner worker. The example reacts to prompt, tool, stop, interrupt, session-end, and subagent lifecycle events.

This repository does not install global hooks automatically. Hook configuration is host-level state and should be reviewed during deployment.

## Fallback signal: process and system activity

With `detectCodexProcessActivity` enabled, a personal worker samples CPU time for `codex` and `codex-*` processes. Any sampled use at or above `codexCpuBusyPercent` resets the quiet window and inhibits work. Ambient system CPU at or above `systemCpuBusyPercent` does the same before a claim. This covers many unhooked workloads but is deliberately secondary: network waits and pure reasoning may consume little local CPU.

## Worker response

When a marker appears during an encode, the worker treats it as higher-priority work. It safely asks HandBrakeCLI to stop, removes only that lease's partial output, reports an interruption, and requeues the job without increasing `failureCount`. It never kills HandBrake behind the worker's back and never edits the coordinator state file directly.

Human keyboard or mouse activity is different. It enters `draining`, stops new claims, and finishes the current video by default while showing Pause and Stop & Requeue controls in the Windows notification area.
