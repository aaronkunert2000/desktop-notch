#!/usr/bin/env python3
"""notch_send.py: sendet Events an das Desktop-Notch (nur localhost, Token-geschützt).

Läuft das Notch nicht, bricht das Skript nach max. 1 s still ab (Exit 0),
damit aufrufende Pipelines/Hooks nie blockieren. Mit --strict gibt es Exit 1.

Beispiele:
  python notch_send.py --source vaultpipeline --kind running --title "VaultPipeline" --subtitle "Dropzone …"
  python notch_send.py --source vaultpipeline --kind done --title "Pipeline fertig" --subtitle "12 Dateien"
  python notch_send.py --source vaultpipeline --kind error --title "Pipeline fehlgeschlagen" --subtitle "Exit 1"
  python notch_send.py --source vaultpipeline --dismiss
  python notch_send.py --source test --kind input_needed --title "Freigabe?" \
      --action approve:Freigeben:primary --action deny:Ablehnen --wait 60
  python notch_send.py --health
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

KINDS: tuple[str, ...] = ("running", "input_needed", "error", "done", "timer", "info")
DEFAULT_PORT: int = 47321
TIMEOUT_S: float = 1.0
POLL_INTERVAL_S: float = 0.5

log = logging.getLogger("notch_send")

# Proxys ignorieren: Ziel ist immer localhost
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def load_token() -> str | None:
    """NOTCH_TOKEN oder die vom Notch erzeugte Datei %APPDATA%\\Notch\\token."""
    env = os.environ.get("NOTCH_TOKEN")
    if env:
        return env.strip()
    data_dir = os.environ.get("NOTCH_DATA_DIR")
    if not data_dir:
        appdata = os.environ.get("APPDATA")
        if not appdata:
            return None
        data_dir = str(Path(appdata) / "Notch")
    try:
        return (Path(data_dir) / "token").read_text(encoding="utf-8").strip()
    except OSError:
        return None


def base_url() -> str:
    port = int(os.environ.get("NOTCH_PORT", DEFAULT_PORT))
    # "localhost", nicht 127.0.0.1: der Server ist auf den Hostnamen localhost registriert
    return f"http://localhost:{port}"


def request(
    method: str, path: str, token: str | None, body: dict[str, Any] | None = None
) -> tuple[int, dict[str, Any] | None]:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(base_url() + path, data=data, method=method)
    req.add_header("Content-Type", "application/json; charset=utf-8")
    if token:
        req.add_header("X-Notch-Token", token)
    with _OPENER.open(req, timeout=TIMEOUT_S) as resp:
        raw = resp.read()
        return resp.status, (json.loads(raw) if raw else None)


def parse_action(spec: str) -> dict[str, Any]:
    """'id:Label' oder 'id:Label:primary'."""
    parts = spec.split(":")
    if len(parts) < 2 or not parts[0] or not parts[1]:
        raise argparse.ArgumentTypeError(f"Aktion '{spec}' muss 'id:Label[:primary]' sein")
    return {"id": parts[0], "label": parts[1], "primary": len(parts) > 2 and parts[2] == "primary"}


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Event an das Desktop-Notch senden")
    p.add_argument("--source", default="script", help="Absender, z. B. vaultpipeline, claude-code")
    p.add_argument("--id", help="Optional: unterscheidet parallele Vorgänge derselben Quelle")
    p.add_argument("--kind", choices=KINDS)
    p.add_argument("--title")
    p.add_argument("--subtitle")
    p.add_argument("--sticky", action=argparse.BooleanOptionalAction, default=None,
                   help="Bleibt stehen bis ersetzt/geschlossen (Default: running/input_needed/error)")
    p.add_argument("--ttl", type=int, help="Sekunden bis zum Einklappen (nicht-sticky), 1–120")
    p.add_argument("--action", action="append", type=parse_action, default=[],
                   help="Button 'id:Label[:primary]', max. 3")
    p.add_argument("--wait", type=float, default=0.0,
                   help="Sekunden auf Button-Klick warten; gibt die Aktions-ID auf stdout aus (Exit 2 = Timeout)")
    p.add_argument("--dismiss", action="store_true", help="Events dieser Quelle (und id) schließen")
    p.add_argument("--health", action="store_true", help="Prüfen, ob das Notch läuft")
    p.add_argument("--strict", action="store_true", help="Exit 1, wenn das Notch nicht erreichbar ist")
    p.add_argument("-v", "--verbose", action="store_true")
    return p


def wait_for_result(source: str, event_id: str | None, token: str | None, seconds: float) -> int:
    query = urllib.parse.urlencode({k: v for k, v in {"source": source, "id": event_id}.items() if v})
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            status, body = request("GET", f"/result?{query}", token)
            if status == 200 and body:
                print(body.get("action", ""))
                return 0
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            log.debug("Poll fehlgeschlagen: %s", exc)
        time.sleep(POLL_INTERVAL_S)
    return 2


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.WARNING,
                        format="notch_send: %(message)s")

    if not (args.health or args.dismiss) and (not args.kind or not args.title):
        parser.error("--kind und --title sind Pflicht (außer bei --dismiss/--health)")

    token = load_token()
    if token is None and not args.health:
        log.debug("Kein Token gefunden (NOTCH_TOKEN oder %APPDATA%\\Notch\\token)")

    try:
        if args.health:
            status, _ = request("GET", "/health", None)
            print("ok" if status == 200 else f"status {status}")
            return 0

        if args.dismiss:
            request("POST", "/dismiss", token, {"source": args.source, "id": args.id})
            return 0

        payload: dict[str, Any] = {
            "source": args.source,
            "id": args.id,
            "kind": args.kind,
            "title": args.title,
            "subtitle": args.subtitle,
            "sticky": args.sticky,
            "ttl": args.ttl,
            "actions": args.action[:3] or None,
        }
        request("POST", "/event", token, {k: v for k, v in payload.items() if v is not None})
    except urllib.error.HTTPError as exc:
        log.warning("Notch lehnt ab (%s): %s", exc.code, exc.read().decode("utf-8", "replace"))
        return 1 if args.strict else 0
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        if args.health:
            print("offline")
        log.debug("Notch nicht erreichbar: %s", exc)
        return 1 if args.strict else 0

    if args.wait > 0:
        return wait_for_result(args.source, args.id, token, args.wait)
    return 0


if __name__ == "__main__":
    sys.exit(main())
