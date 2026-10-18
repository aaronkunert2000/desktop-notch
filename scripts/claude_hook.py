#!/usr/bin/env python3
"""claude_hook.py: Claude-Code-Hook → Desktop-Notch.

Claude Code ruft das Skript bei Hook-Ereignissen auf und übergibt die Daten als JSON über stdin.
Das Skript schreibt NICHTS auf stdout (bei UserPromptSubmit würde Claude das als Kontext lesen),
blockiert nie und endet immer mit Exit 0.
"""
from __future__ import annotations

import json
import logging
import os
import sys
import urllib.error
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from notch_send import load_token, request  # noqa: E402

SOURCE = "claude-code"
log = logging.getLogger("claude_hook")


def project_name(event: dict[str, Any]) -> str:
    cwd = event.get("cwd") or os.getcwd()
    return Path(cwd).name or "Claude Code"


def build(event: dict[str, Any]) -> tuple[str, dict[str, Any]] | None:
    """Übersetzt ein Hook-Ereignis in (Pfad, Body) für den Notch-Server."""
    name = event.get("hook_event_name", "")
    session = str(event.get("session_id") or "")[:8] or None
    project = project_name(event)
    # Elternprozess (Claude Code bzw. dessen Shell): das Notch sucht darüber das Terminal-Fenster
    base: dict[str, Any] = {"source": SOURCE, "id": session, "hostPid": os.getppid()}

    if name == "UserPromptSubmit":
        return "/event", {**base, "kind": "running", "title": "Claude arbeitet", "subtitle": project}
    if name == "PreToolUse":
        tool = event.get("tool_name") or "Werkzeug"
        return "/event", {**base, "kind": "running", "title": "Claude arbeitet", "subtitle": f"{project} · {tool}"}
    if name == "Notification":
        message = event.get("message") or "Claude wartet auf dich"
        return "/event", {**base, "kind": "input_needed", "title": "Claude wartet", "subtitle": f"{project} · {message}"}
    if name == "Stop":
        return "/event", {**base, "kind": "done", "title": "Claude fertig", "subtitle": project, "ttl": 5}
    if name == "SessionEnd":
        return "/dismiss", base
    return None


def main() -> int:
    logging.basicConfig(level=logging.DEBUG if os.environ.get("NOTCH_HOOK_DEBUG") else logging.CRITICAL,
                        format="claude_hook: %(message)s", stream=sys.stderr)
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError) as exc:
        log.debug("Kein gültiges Hook-JSON: %s", exc)
        return 0

    built = build(event) if isinstance(event, dict) else None
    if built is None:
        return 0

    path, body = built
    try:
        request("POST", path, load_token(), {k: v for k, v in body.items() if v is not None})
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        log.debug("Notch nicht erreichbar: %s", exc)
    return 0


if __name__ == "__main__":
    sys.exit(main())
