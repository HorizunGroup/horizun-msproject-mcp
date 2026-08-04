#!/usr/bin/env python3
"""How the server behaves when it is used badly.

Concurrency, malformed arguments, hostile paths, absurd values. A public tool is judged
on this as much as on its features: a crash, a hang, or an error that says nothing are
all worse than a missing feature.

    python tools/robustness-test.py
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_BIN = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0"
EXE = _BIN / ("horizun-msproject-mcp.exe" if sys.platform == "win32" else "horizun-msproject-mcp")

# The SDK prefixes every tool failure with this; the message that matters follows it.
SDK_PREFIX = "An error occurred invoking"

CHECKS = 0
FAILURES: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    global CHECKS
    CHECKS += 1
    if not ok:
        FAILURES.append(label)
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}{f' -- {detail}' if detail else ''}")


class Pipelined:
    """A client that can hold many requests in flight, which is how concurrency actually arises."""

    def __init__(self, exe: Path) -> None:
        self.proc = subprocess.Popen(
            [str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        self.replies: dict[int, dict] = {}
        self._id = 0
        self._lock = threading.Lock()
        self._stop = threading.Event()
        threading.Thread(target=self._read, daemon=True).start()

        self.send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "robustness", "version": "1"}})
        self.wait_all(timeout=30)
        with self._lock:
            self.proc.stdin.write(
                json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
            self.proc.stdin.flush()

    def _read(self) -> None:
        while not self._stop.is_set():
            line = self.proc.stdout.readline()
            if not line:
                return
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") is not None:
                self.replies[msg["id"]] = msg

    def send(self, method: str, params: dict) -> int:
        with self._lock:
            self._id += 1
            mine = self._id
            self.proc.stdin.write(
                json.dumps({"jsonrpc": "2.0", "id": mine, "method": method, "params": params}) + "\n")
            self.proc.stdin.flush()
        return mine

    def send_call(self, tool: str, **args) -> int:
        return self.send("tools/call", {"name": tool, "arguments": args})

    def wait(self, ids: list[int], timeout: float = 120) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if all(i in self.replies for i in ids):
                return True
            time.sleep(0.02)
        return False

    def wait_all(self, timeout: float = 120) -> bool:
        return self.wait(list(range(1, self._id + 1)), timeout)

    def call(self, tool: str, /, **args):
        i = self.send_call(tool, **args)
        if not self.wait([i], 90):
            raise TimeoutError(f"{tool} did not answer")
        msg = self.replies[i]
        if "error" in msg:
            return {"__error__": json.dumps(msg["error"])}
        result = msg["result"]
        text = result["content"][0]["text"] if result.get("content") else "{}"
        if result.get("isError"):
            return {"__error__": text}
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return {"__raw__": text}

    def alive(self) -> bool:
        return self.proc.poll() is None

    def close(self) -> None:
        self._stop.set()
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


def explains_itself(message: str) -> bool:
    """An error is only useful if something follows the SDK's prefix."""
    body = message.split(":", 1)[1].strip() if SDK_PREFIX in message and ":" in message else message
    return len(body) >= 25


def main() -> int:
    if not EXE.exists():
        print(f"FAIL  build first: {EXE} not found", file=sys.stderr)
        return 1

    work = Path(tempfile.mkdtemp(prefix="hzpm-robust-"))
    c = Pipelined(EXE)

    try:
        h = c.call("project_open", path=str(work / "seed.xml"), create=True,
                   startDate="2026-09-07")["handle"]
        c.call("tasks_write", handle=h, ops=[{"op": "create", "name": "A", "duration": "3d"}])

        # ------------------------------------------------------- malformed input
        print("\n== bad arguments are refused with a reason ==")
        cases = [
            ("project_open", {"path": ""}, "an empty path"),
            ("project_open", {"path": "   "}, "a whitespace path"),
            ("project_open", {"path": str(work)}, "a directory instead of a file"),
            ("project_open", {"path": str(work / "missing.mpp")}, "a file that is not there"),
            ("tasks_query", {"handle": "nope"}, "an unknown handle"),
            ("schedule_target", {"handle": h, "target": "not a date"}, "an unparseable date"),
            ("timephased_query", {"handle": h, "from": "2020-01-01", "to": "1990-01-01"},
             "a reversed date range"),
            ("schedule_learn", {"paths": []}, "no schedules to learn from"),
            ("project_export", {"handle": h, "path": str(work / "x"), "format": "invented"},
             "an unknown export format"),
            ("bim_sync", {"handle": h, "direction": "sideways"}, "an unknown direction"),
        ]

        for tool, args, label in cases:
            r = c.call(tool, **args)
            message = str(r.get("__error__", ""))
            check(f"{tool} refuses {label} and says why",
                  "__error__" in r and explains_itself(message),
                  message[:90])

        check("the server is still alive after all of that", c.alive())

        # --------------------------------------------------------- absurd values
        print("\n== absurd values are clamped rather than fatal ==")
        for label, args in (
            ("a negative page size", {"limit": -5}),
            ("a page size of a billion", {"limit": 1_000_000_000}),
            ("a negative cursor", {"cursor": -20}),
            ("a cursor past the end", {"cursor": 999_999}),
        ):
            r = c.call("tasks_query", handle=h, **args)
            check(f"tasks_query survives {label}", "__error__" not in r,
                  str(r.get("__error__"))[:70])

        # ------------------------------------------------------------ concurrency
        print("\n== many calls in flight at once on one document ==")
        writes = 40
        ids = [c.send_call("tasks_write", handle=h, ops=[
            {"op": "create", "name": f"T{n:03}", "duration": "2d"}]) for n in range(writes)]
        # Reads and analyses interleaved with the writes, against the same document.
        for _ in range(15):
            ids.append(c.send_call("tasks_query", handle=h, limit=100))
            ids.append(c.send_call("schedule_analyze", handle=h, aspects=["critical_path"]))

        answered = c.wait(ids, timeout=180)
        check("every call in flight is answered", answered,
              f"{sum(1 for i in ids if i in c.replies)}/{len(ids)}")
        check("the server survived it", c.alive())

        errored = [i for i in ids if i in c.replies and
                   (c.replies[i].get("error")
                    or c.replies[i].get("result", {}).get("isError"))]
        check("none of them failed", not errored,
              json.dumps(c.replies[errored[0]])[:130] if errored else "")

        rows = c.call("tasks_query", handle=h, limit=500)["items"]
        names = {t["name"] for t in rows}
        missing = {f"T{n:03}" for n in range(writes)} - names
        check("no write was lost", not missing, f"{len(missing)} missing")

        uids = [t["uid"] for t in rows]
        check("no two tasks share a uid", len(uids) == len(set(uids)),
              f"{len(uids) - len(set(uids))} duplicates")

        # -------------------------------------------------------- nested tool calls
        print("\n== tools built on other tools do not deadlock ==")

        # schedule_sequence applies through links_write and project_import through tasks_write.
        # Both run inside the document's lock, so a non-reentrant lock would hang the server here
        # rather than fail — the worst way for this to break.
        nest = work / "nested.xml"
        hn = c.call("project_open", path=str(nest), create=True, startDate="2026-09-07")["handle"]
        ops = []
        for unit in (1, 2, 3):
            for step, trade in enumerate(("Uno", "Dos", "Tres")):
                ops.append({"op": "create", "name": f"{trade} apto {unit}0{unit}", "duration": "2d",
                            "constraintType": "StartNoEarlierThan",
                            "constraintDate": f"2026-09-{7 + step * 3 + (unit - 1):02}"})
        c.call("tasks_write", handle=hn, ops=ops)

        seq_id = c.send_call("schedule_sequence", handle=hn, groupDepth=1, apply=True)
        check("schedule_sequence applying through links_write answers rather than hanging",
              c.wait([seq_id], timeout=90), "no reply within 90s")

        progress = work / "progress.csv"
        uid = c.call("tasks_query", handle=hn, limit=1)["items"][0]["uid"]
        progress.write_text(f"uid,percentComplete\n{uid},50\n", encoding="utf-8")
        imp_id = c.send_call("project_import", handle=hn, path=str(progress), apply=True)
        check("project_import applying through tasks_write answers rather than hanging",
              c.wait([imp_id], timeout=90), "no reply within 90s")
        check("the server is still responsive afterwards",
              c.call("project_info", handle=hn).get("tasks", 0) > 0)
        c.call("project_save", handle=hn, op="close", discardChanges=True)

        # ------------------------------------------------- unusual but legal input
        print("\n== unusual but legal input ==")
        odd = c.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Ω≈ç√ 中文 — émoji 🏗", "duration": "1d"},
            {"op": "create", "name": "x" * 400, "duration": "0.01d"},
        ])
        check("non-latin names and a 400-character name are accepted",
              odd.get("applied") == 2, json.dumps(odd.get("rejected"))[:110])

        huge = c.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Very long", "duration": "99999d"}])
        check("an absurd duration is accepted rather than crashing",
              huge.get("applied") == 1, json.dumps(huge.get("rejected"))[:110])

        check("the document still reads back cleanly",
              c.call("project_info", handle=h).get("tasks", 0) > 0)

        # Operations that were once advertised but never worked must refuse by name, so nobody
        # has to discover from a Gantt chart that nothing happened.
        uid = c.call("tasks_query", handle=h, limit=1)["items"][0]["uid"]
        moved = c.call("tasks_write", handle=h, ops=[{"op": "move", "uid": uid, "afterUid": uid}])
        check("a removed operation refuses by name instead of half-working",
              moved.get("applied") == 0
              and any("no 'move' operation" in r["reason"] for r in moved.get("rejected", [])),
              json.dumps(moved.get("rejected"))[:120])

        hours = c.call("calendars_write", handle=h, ops=[
            {"op": "set_working_hours", "name": "Standard"}])
        check("so does the working-hours pattern nobody can edit here",
              hours.get("applied") == 0
              and any("not editable" in r["reason"] for r in hours.get("rejected", [])),
              json.dumps(hours.get("rejected"))[:120])

    finally:
        try:
            c.close()
        except Exception:
            pass
        shutil.rmtree(work, ignore_errors=True)

    print(f"\n=== {CHECKS - len(FAILURES)}/{CHECKS} checks passed ===")
    if FAILURES:
        print("failed:")
        for f in FAILURES:
            print(f"  - {f}")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
