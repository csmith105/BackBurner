#!/usr/bin/env python3
"""Create per-session BackBurner inhibit markers, directly or from Codex hooks."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import uuid


DEFAULT_TTL_HOURS = 24
START_EVENTS = {"UserPromptSubmit", "PreToolUse", "SubagentStart"}
STOP_EVENTS = {"Stop", "Interrupt", "SubagentStop", "SessionEnd"}


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso(value: datetime) -> str:
    return value.isoformat().replace("+00:00", "Z")


def marker_directory() -> Path:
    override = os.environ.get("BACKBURNER_INHIBIT_DIR")
    if override:
        return Path(os.path.expandvars(override)).expanduser()
    if os.name == "nt":
        base = os.environ.get("LOCALAPPDATA")
        if not base:
            raise RuntimeError("LOCALAPPDATA is unavailable; set BACKBURNER_INHIBIT_DIR.")
        return Path(base) / "BackBurner" / "inhibits"
    return Path.home() / ".local" / "state" / "backburner" / "inhibits"


def safe_id(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "-", value).strip("-.")
    return cleaned[:160] or str(uuid.uuid4())


def marker_path(marker_id: str) -> Path:
    return marker_directory() / f"{safe_id(marker_id)}.json"


def write_atomic(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, path)
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


def acquire(marker_id: str, owner: str, reason: str, ttl_hours: float) -> Path:
    now = utc_now()
    path = marker_path(marker_id)
    write_atomic(
        path,
        {
            "schema_version": 1,
            "marker_id": safe_id(marker_id),
            "owner": owner,
            "reason": reason,
            "pid": os.getpid(),
            "updated_at": iso(now),
            "expires_at": iso(now + timedelta(hours=ttl_hours)),
        },
    )
    return path


def release(marker_id: str) -> bool:
    path = marker_path(marker_id)
    try:
        path.unlink()
        return True
    except FileNotFoundError:
        return False


def read_marker(path: Path) -> dict[str, object] | None:
    try:
        with path.open("r", encoding="utf-8") as stream:
            value = json.load(stream)
        return value if isinstance(value, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


def expiry(marker: dict[str, object]) -> datetime | None:
    value = marker.get("expires_at")
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def prune() -> int:
    directory = marker_directory()
    if not directory.exists():
        return 0
    removed = 0
    now = utc_now()
    for path in directory.glob("*.json"):
        marker = read_marker(path)
        expires_at = expiry(marker) if marker else None
        if expires_at is not None and expires_at <= now:
            try:
                path.unlink()
                removed += 1
            except FileNotFoundError:
                pass
    return removed


def marker_id_for_event(event: dict[str, object]) -> str:
    session = str(event.get("session_id") or event.get("conversation_id") or "codex")
    agent = str(event.get("agent_id") or "main")
    return safe_id(f"codex-{session}-{agent}")


def release_session_markers(session: str) -> int:
    directory = marker_directory()
    if not directory.exists():
        return 0
    prefix = f"codex-{safe_id(session)}-"
    removed = 0
    for path in directory.glob(f"{prefix}*.json"):
        try:
            path.unlink()
            removed += 1
        except FileNotFoundError:
            pass
    return removed


def handle_hook() -> int:
    try:
        event = json.load(sys.stdin)
    except json.JSONDecodeError:
        print("BackBurner hook received invalid JSON.", file=sys.stderr)
        return 0

    event_name = str(event.get("hook_event_name") or event.get("event") or "")
    marker_id = marker_id_for_event(event)
    if event_name in START_EVENTS:
        acquire(marker_id, f"Codex {event.get('session_id', 'session')}", f"Codex event: {event_name}", DEFAULT_TTL_HOURS)
    elif event_name == "SessionEnd":
        release_session_markers(str(event.get("session_id") or event.get("conversation_id") or "codex"))
    elif event_name in STOP_EVENTS:
        release(marker_id)
    prune()
    # Stop-style hooks expect JSON. PreToolUse does not accept `continue`, while
    # Interrupt and SessionEnd are safest with no output, so stay event-specific.
    if event_name in {"UserPromptSubmit", "Stop", "SubagentStop"}:
        print(json.dumps({"continue": True}))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    acquire_parser = subparsers.add_parser("acquire")
    acquire_parser.add_argument("--marker-id", default=None)
    acquire_parser.add_argument("--owner", required=True)
    acquire_parser.add_argument("--reason", default="Higher-priority host work")
    acquire_parser.add_argument("--ttl-hours", type=float, default=DEFAULT_TTL_HOURS)

    release_parser = subparsers.add_parser("release")
    release_parser.add_argument("--marker-id", required=True)

    subparsers.add_parser("status")
    subparsers.add_parser("prune")
    subparsers.add_parser("hook")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.command == "hook":
        return handle_hook()
    if args.command == "acquire":
        marker_id = args.marker_id or safe_id(f"manual-{uuid.uuid4()}")
        path = acquire(marker_id, args.owner, args.reason, args.ttl_hours)
        print(json.dumps({"marker_id": safe_id(marker_id), "path": str(path)}))
        return 0
    if args.command == "release":
        print(json.dumps({"released": release(args.marker_id)}))
        return 0
    if args.command == "prune":
        print(json.dumps({"removed": prune()}))
        return 0

    prune()
    directory = marker_directory()
    markers = [read_marker(path) for path in directory.glob("*.json")] if directory.exists() else []
    print(json.dumps([marker for marker in markers if marker is not None], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
