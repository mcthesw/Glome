"""Send a local live-debug command to an open Godot Editor running GlomeEditorPlugin."""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import tempfile
import time
import uuid
from pathlib import Path

COMMANDS = ("status", "snapshot", "rebake", "reload_shader", "reload_scene", "focus_node", "set_camera")


def write_atomic(path: Path, payload: dict) -> None:
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=f"{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, allow_nan=False)
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def try_lock(lock) -> None:
    if os.name == "nt":
        import msvcrt

        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
    else:
        import fcntl

        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)


def send(bridge_dir: Path, command: str, args: dict, timeout: float) -> dict:
    bridge_dir.mkdir(parents=True, exist_ok=True)
    pending = bridge_dir / "editor-cmd.json"
    response = bridge_dir / "editor-ack.json"
    request_id = uuid.uuid4().hex
    with (bridge_dir / "editor.lock").open("a+b") as lock:
        try:
            try_lock(lock)
        except OSError as error:
            raise RuntimeError("Another CLI command is waiting for the Glome editor bridge.") from error
        if pending.exists():
            raise RuntimeError(f"Previous request at {pending} has not been consumed yet.")
        write_atomic(pending, {**args, "id": request_id, "cmd": command})
        deadline = time.monotonic() + timeout
        try:
            while time.monotonic() < deadline:
                try:
                    ack = json.loads(response.read_bytes())
                    if ack.get("id") == request_id:
                        return ack
                except (OSError, ValueError):
                    pass
                time.sleep(min(0.05, max(0, deadline - time.monotonic())))
        finally:
            try:
                if json.loads(pending.read_bytes()).get("id") == request_id:
                    pending.unlink(missing_ok=True)
            except (OSError, ValueError):
                pass
    raise TimeoutError(f"No editor acknowledgement within {timeout:g}s in {bridge_dir}.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cmd", choices=COMMANDS)
    parser.add_argument("--dir", type=Path, default=Path.cwd() / ".local" / "glome-bridge",
                        help="Bridge directory (default: <cwd>/.local/glome-bridge)")
    parser.add_argument("--json", default="{}", help="JSON object with command arguments")
    parser.add_argument("--timeout", type=float, default=5)
    options = parser.parse_args()
    if not math.isfinite(options.timeout) or options.timeout <= 0:
        parser.error("--timeout must be positive and finite")
    try:
        args = json.loads(options.json)
        if not isinstance(args, dict):
            raise ValueError("--json must be a JSON object.")
        ack = send(options.dir, options.cmd, args, options.timeout)
    except (OSError, RuntimeError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    print(json.dumps(ack, indent=2, ensure_ascii=True))
    return 0 if ack.get("ok") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
