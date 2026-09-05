---
name: backburner-host-control
description: Coordinate substantial Codex or developer workloads with a local BackBurner worker. Use when a Codex agent may compile, test, render, run a game engine, or otherwise consume meaningful CPU, GPU, memory, or disk on a machine that also runs BackBurner.
---

# BackBurner Host Control

BackBurner is the lowest-priority workload on a shared host. Acquire an inhibit marker before substantial local work and release only the marker this session owns when finished or interrupted.

Use the bundled `scripts/backburner_inhibit.py` helper. Run `acquire --owner <stable-session-name> --reason <short-reason>` before meaningful host work. Keep the returned marker ID and run `release --marker-id <id>` in cleanup. Never delete the whole inhibit directory and never release another session's marker.

Prefer installing the helper as Codex lifecycle hooks so prompt, tool, subagent, stop, and interrupt events maintain markers automatically. The hook command reads a hook event from standard input:

```text
python scripts/backburner_inhibit.py hook
```

If hooks are unavailable, use explicit `acquire` and `release`. BackBurner also watches Codex and system CPU as a conservative fallback, but explicit markers are the reliable signal.

Do not kill HandBrake or edit BackBurner state directly. Once inhibited, the worker owns the safe handoff: it stops claiming work, pauses or requeues interruptible work, and preserves retry accounting and fencing.
