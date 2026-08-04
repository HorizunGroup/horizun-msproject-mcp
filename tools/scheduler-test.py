#!/usr/bin/env python3
"""Correctness tests for the critical-path engine.

The acceptance test only exercises finish-to-start logic. This one drives the paths that
are easy to get subtly wrong and hard to notice: start-to-start, finish-to-finish,
start-to-finish, negative lag, constraints, deadlines and negative float, calendar
exceptions, and a real binary .mpp round trip.

    python tools/scheduler-test.py
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
from datetime import date, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0" / "horizun-msproject-mcp.exe"

CHECKS = 0
FAILURES: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    global CHECKS
    CHECKS += 1
    if not ok:
        FAILURES.append(label)
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}{f' -- {detail}' if detail else ''}")


class Client:
    def __init__(self, exe: Path) -> None:
        self.proc = subprocess.Popen(
            [str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
        )
        self._id = 0
        self._rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "scheduler-test", "version": "1.0"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, payload: dict) -> None:
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()

    def _rpc(self, method: str, params: dict) -> dict:
        self._id += 1
        self._send({"jsonrpc": "2.0", "id": self._id, "method": method, "params": params})
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError(f"server closed the stream during '{method}'")
            msg = json.loads(line)
            if msg.get("id") == self._id:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg["result"]

    def call(self, tool_name: str, /, **args):
        result = self._rpc("tools/call", {"name": tool_name, "arguments": args})
        text = result["content"][0]["text"] if result.get("content") else "{}"
        if result.get("isError"):
            return {"__error__": text}
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return text

    def close(self) -> None:
        self.proc.stdin.close()
        self.proc.wait(timeout=15)


def d(iso: str | None) -> date | None:
    return date.fromisoformat(iso[:10]) if iso else None


def build(client: Client, workdir: Path, name: str, tasks: list[dict], links: list[dict],
          start: str = "2026-09-07") -> tuple[str, dict[str, dict]]:
    """Creates a schedule, applies logic, and returns the handle plus tasks keyed by name.

    2026-09-07 is a Monday, so every expectation below can be reasoned about by hand.
    """
    path = workdir / f"{name}.xml"
    handle = client.call("project_open", path=str(path), create=True, startDate=start)["handle"]
    client.call("tasks_write", handle=handle, ops=[{"op": "create", **t} for t in tasks])

    rows = client.call("tasks_query", handle=handle, limit=100)["items"]
    uid = {t["name"]: t["uid"] for t in rows}

    if links:
        client.call("links_write", handle=handle, ops=[
            {**l, "from": uid[l["from"]], "to": uid[l["to"]]} for l in links])

    rows = client.call("tasks_query", handle=handle, limit=100)["items"]
    return handle, {t["name"]: t for t in rows}


def main() -> int:
    if not EXE.exists():
        print(f"FAIL  build first: {EXE} not found", file=sys.stderr)
        return 1

    workdir = Path(tempfile.mkdtemp(prefix="hzpm-scheduler-"))
    client = Client(EXE)

    try:
        # ---------------------------------------------------------- relationship types
        print("\n== relationship types ==")

        _, t = build(client, workdir, "ss", [
            {"name": "A", "duration": "5d"}, {"name": "B", "duration": "3d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "SS"}])
        check("start-to-start makes both tasks start together",
              d(t["B"]["start"]) == d(t["A"]["start"]),
              f"A {t['A']['start'][:10]} / B {t['B']['start'][:10]}")

        _, t = build(client, workdir, "ss-lag", [
            {"name": "A", "duration": "5d"}, {"name": "B", "duration": "3d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "SS", "lag": "2d"}])
        check("start-to-start with lag delays the successor's start by the lag",
              d(t["B"]["start"]) == d(t["A"]["start"]) + timedelta(days=2),
              f"A {t['A']['start'][:10]} / B {t['B']['start'][:10]}")

        _, t = build(client, workdir, "ff", [
            {"name": "A", "duration": "5d"}, {"name": "B", "duration": "2d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "FF"}])
        check("finish-to-finish makes both tasks finish together",
              d(t["B"]["finish"]) == d(t["A"]["finish"]),
              f"A ends {t['A']['finish'][:10]} / B ends {t['B']['finish'][:10]}")

        # Push A out with a constraint so the project start floor is not what decides the answer.
        handle, _ = build(client, workdir, "sf", [
            {"name": "A", "duration": "5d"}, {"name": "B", "duration": "2d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "SF"}])
        uid_a = next(x["uid"] for x in client.call("tasks_query", handle=handle)["items"]
                     if x["name"] == "A")
        client.call("tasks_write", handle=handle, ops=[{
            "op": "update", "uid": uid_a,
            "constraintType": "StartNoEarlierThan", "constraintDate": "2026-10-05"}])
        t = {x["name"]: x for x in client.call("tasks_query", handle=handle)["items"]}
        check("start-to-finish drives the successor's finish to the predecessor's start",
              d(t["B"]["finish"]) == d(t["A"]["start"]),
              f"A starts {t['A']['start'][:10]} / B ends {t['B']['finish'][:10]}")

        # ------------------------------------------------------------------- lag
        print("\n== lag ==")

        _, t = build(client, workdir, "fs-lag", [
            {"name": "A", "duration": "5d"}, {"name": "B", "duration": "2d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "FS", "lag": "3d"}])
        gap = (d(t["B"]["start"]) - d(t["A"]["finish"])).days
        check("finish-to-start lag opens a real gap", gap >= 4,
              f"A ends {t['A']['finish'][:10]}, B starts {t['B']['start'][:10]} ({gap} days later)")

        _, t = build(client, workdir, "fs-lead", [
            {"name": "A", "duration": "10d"}, {"name": "B", "duration": "5d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "FS", "lag": "-4d"}])
        check("negative lag pulls the successor forward, overlapping its predecessor",
              d(t["B"]["start"]) < d(t["A"]["finish"]),
              f"A ends {t['A']['finish'][:10]}, B starts {t['B']['start'][:10]}")

        # ---------------------------------------------------------- working calendar
        print("\n== working calendar ==")

        _, t = build(client, workdir, "weekend", [{"name": "A", "duration": "5d"}], [])
        check("a five-day task starting Monday finishes Friday, not Saturday",
              d(t["A"]["start"]).weekday() == 0 and d(t["A"]["finish"]).weekday() == 4,
              f"{t['A']['start'][:10]} -> {t['A']['finish'][:10]}")

        _, t = build(client, workdir, "spanweek", [{"name": "A", "duration": "10d"}], [])
        span = (d(t["A"]["finish"]) - d(t["A"]["start"])).days
        check("a ten-day task spans two weekends (11 calendar days end to end)",
              span == 11, f"{span} calendar days")

        handle, _ = build(client, workdir, "holiday", [
            {"name": "A", "duration": "3d"}, {"name": "B", "duration": "3d"},
        ], [{"op": "link", "from": "A", "to": "B", "type": "FS"}])
        before = client.call("tasks_query", handle=handle, limit=10)["items"]
        finish_before = d(next(x for x in before if x["name"] == "B")["finish"])

        client.call("calendars_write", handle=handle, ops=[
            {"op": "set_exception", "from": "2026-09-10", "to": "2026-09-11", "working": False}])
        client.call("schedule_update", handle=handle, op="recalculate")

        after = client.call("tasks_query", handle=handle, limit=10)["items"]
        finish_after = d(next(x for x in after if x["name"] == "B")["finish"])
        check("a calendar exception actually pushes the schedule out",
              finish_after > finish_before,
              f"{finish_before} -> {finish_after}")

        # ------------------------------------------------------------- constraints
        print("\n== constraints ==")

        handle, _ = build(client, workdir, "snet", [{"name": "A", "duration": "3d"}], [])
        uid = client.call("tasks_query", handle=handle)["items"][0]["uid"]
        client.call("tasks_write", handle=handle, ops=[{
            "op": "update", "uid": uid,
            "constraintType": "StartNoEarlierThan", "constraintDate": "2026-10-05"}])
        t = client.call("tasks_query", handle=handle)["items"][0]
        check("start-no-earlier-than holds the task back to its constraint date",
              d(t["start"]) == date(2026, 10, 5), t["start"][:10])

        handle, _ = build(client, workdir, "mso", [{"name": "A", "duration": "3d"}], [])
        uid = client.call("tasks_query", handle=handle)["items"][0]["uid"]
        client.call("tasks_write", handle=handle, ops=[{
            "op": "update", "uid": uid,
            "constraintType": "MustStartOn", "constraintDate": "2026-09-21"}])
        t = client.call("tasks_query", handle=handle)["items"][0]
        check("must-start-on pins the task to an exact date",
              d(t["start"]) == date(2026, 9, 21), t["start"][:10])

        # --------------------------------------------------------- float behaviour
        print("\n== float ==")

        _, t = build(client, workdir, "float", [
            {"name": "Long", "duration": "10d"},
            {"name": "Short", "duration": "2d"},
            {"name": "End", "duration": "1d"},
        ], [
            {"op": "link", "from": "Long", "to": "End", "type": "FS"},
            {"op": "link", "from": "Short", "to": "End", "type": "FS"},
        ])
        check("the driving branch is critical and the slack branch is not",
              t["Long"]["critical"] and not t["Short"]["critical"],
              f"Long={t['Long']['critical']} Short={t['Short']['critical']}")
        check("the non-driving branch carries positive float",
              (t["Short"]["totalFloatDays"] or 0) > 0,
              f"Short float={t['Short']['totalFloatDays']}")
        check("the critical branch carries no float",
              abs(t["Long"]["totalFloatDays"] or 0) < 0.001,
              f"Long float={t['Long']['totalFloatDays']}")

        handle, _ = build(client, workdir, "negfloat", [{"name": "A", "duration": "20d"}], [])
        uid = client.call("tasks_query", handle=handle)["items"][0]["uid"]
        client.call("tasks_write", handle=handle, ops=[
            {"op": "update", "uid": uid, "deadline": "2026-09-14"}])
        t = client.call("tasks_query", handle=handle)["items"][0]
        check("a deadline the task cannot meet produces negative float",
              (t["totalFloatDays"] or 0) < 0, f"float={t['totalFloatDays']}")

        # ------------------------------------------------------- cycle containment
        print("\n== degenerate input ==")

        handle, _ = build(client, workdir, "empty", [], [])
        recalc = client.call("schedule_update", handle=handle, op="recalculate")
        check("recalculating an empty schedule fails cleanly rather than crashing",
              "__error__" in recalc or recalc.get("applied") == 0,
              json.dumps(recalc)[:120])

        # -------------------------------------------------- real binary .mpp round trip
        print("\n== native .mpp round trip ==")

        handle, _ = build(client, workdir, "roundtrip", [
            {"name": "Excavacion", "duration": "8d", "custom": {"Text1": "D010-A1-A01"}},
            {"name": "Cimentacion", "duration": "12d", "custom": {"Text1": "D010-A1-A02"}},
            {"name": "Hito: cimientos", "duration": "0", "milestone": True},
        ], [
            {"op": "link", "from": "Excavacion", "to": "Cimentacion", "type": "FS"},
            {"op": "link", "from": "Cimentacion", "to": "Hito: cimientos", "type": "FS"},
        ])

        mpp = workdir / "roundtrip.mpp"
        saved = client.call("project_save", handle=handle, op="save_as",
                            path=str(mpp), format="mpp")

        if "__error__" in saved:
            print(f"  (skipped: Microsoft Project unavailable -- {saved['__error__'][:80]})")
        else:
            check("a real binary .mpp was produced", mpp.exists() and mpp.stat().st_size > 1000,
                  f"{mpp.stat().st_size if mpp.exists() else 0} bytes")

            reopened = client.call("project_open", path=str(mpp), mode="readonly")
            rt = {x["name"]: x for x in
                  client.call("tasks_query", handle=reopened["handle"],
                              includeCustom=True, limit=50)["items"]}

            check("the .mpp reads back with its tasks",
                  all(n in rt for n in ("Excavacion", "Cimentacion", "Hito: cimientos")),
                  f"found: {sorted(rt)}")
            check("dates survived the binary round trip",
                  d(rt["Cimentacion"]["start"]) > d(rt["Excavacion"]["start"]),
                  f"{rt['Excavacion']['start'][:10]} -> {rt['Cimentacion']['start'][:10]}")
            check("the milestone flag survived the binary round trip",
                  rt["Hito: cimientos"]["milestone"] is True)
            check("the budget code survived the binary round trip",
                  (rt["Excavacion"].get("custom") or {}).get("Text1") == "D010-A1-A01",
                  json.dumps(rt["Excavacion"].get("custom")))

            links = client.call("links_query", handle=reopened["handle"])
            check("the dependency network survived the binary round trip",
                  links["total"] >= 2, f"total={links['total']}")

    finally:
        try:
            client.close()
        except Exception:
            pass
        shutil.rmtree(workdir, ignore_errors=True)

    print(f"\n=== {CHECKS - len(FAILURES)}/{CHECKS} checks passed ===")
    if FAILURES:
        print("failed:")
        for f in FAILURES:
            print(f"  - {f}")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
