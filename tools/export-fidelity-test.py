#!/usr/bin/env python3
"""Round trip: what an export writes must be what it claims to have written.

Builds a schedule shaped like the one that exposed the bug — a baseline, a status date, finished,
in-progress and unstarted tasks — exports it, reopens the file, and compares progress, actual dates,
baseline and status date task by task. It also checks the export itself says so: a format that keeps
everything must report it verified; one that drops something must warn, not report plain success.

Runs on the internal engine and never starts Microsoft Project, so it is safe on any machine. The
native .mpp path needs Project and is covered by project-engine-test.py.

    python tools/export-fidelity-test.py
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_BIN = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0"
EXE = _BIN / ("horizun-msproject-mcp.exe" if sys.platform == "win32" else "horizun-msproject-mcp")

passed = 0
failures: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    global passed
    if ok:
        passed += 1
        print(f"  [PASS] {name}")
    else:
        failures.append(name)
        print(f"  [FAIL] {name}{' -- ' + detail if detail else ''}")


class Mcp:
    def __init__(self) -> None:
        self.p = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
                                  env={**os.environ, "HORIZUN_MSPROJECT_ENGINE": "internal"})
        self.i = 0
        self.req("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                "clientInfo": {"name": "export-fidelity-test", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def req(self, method: str, params: dict) -> dict:
        self.i += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.i, "method": method, "params": params}) + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("server closed the connection")
            if line.strip().startswith("{"):
                m = json.loads(line)
                if m.get("id") == self.i:
                    return m

    def call(self, tool: str, **args) -> dict:
        r = self.req("tools/call", {"name": tool, "arguments": args})
        if "error" in r or r["result"].get("isError"):
            raise RuntimeError(f"{tool}: {r.get('error') or r['result']['content'][0]['text'][:300]}")
        return json.loads(r["result"]["content"][0]["text"])

    def close(self) -> None:
        self.p.stdin.close()
        self.p.terminate()


def state(mcp: Mcp, handle: str) -> dict:
    items = mcp.call("tasks_query", handle=handle, limit=1000)["items"]
    return {t["uid"]: t for t in items if not t["summary"]}


def summary(tasks: dict) -> tuple[int, int, int]:
    done = sum(1 for t in tasks.values() if (t.get("percentComplete") or 0) >= 100)
    going = sum(1 for t in tasks.values() if 0 < (t.get("percentComplete") or 0) < 100)
    return done, going, len(tasks) - done - going


def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-fidelity-"))
    mcp = Mcp()
    try:
        print("build: 60 tasks in a chain, baseline 0, 18 finished, 2 in progress, status date 2027-02-28")
        h = mcp.call("project_open", path=str(work / "source.xml"), create=True,
                     name="Fidelity", startDate="2026-11-02")["handle"]
        mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": f"Actividad {n:02}", "duration": f"{2 + n % 4}d"} for n in range(60)])
        uids = [t["uid"] for t in sorted(state(mcp, h).values(), key=lambda t: t["uid"])]
        mcp.call("links_write", handle=h, ops=[
            {"op": "link", "from": a, "to": b} for a, b in zip(uids, uids[1:])])
        mcp.call("schedule_update", handle=h, op="save_baseline")
        mcp.call("tasks_write", handle=h, ops=
                 [{"op": "update", "uid": u, "percentComplete": 100} for u in uids[:18]]
                 + [{"op": "update", "uid": uids[18], "percentComplete": 60},
                    {"op": "update", "uid": uids[19], "percentComplete": 25}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2027-02-28")
        before = state(mcp, h)
        info_before = mcp.call("project_info", handle=h)
        check("the source has the expected progress", summary(before) == (18, 2, 40), str(summary(before)))
        check("the source has the status date", (info_before.get("statusDate") or "")[:10] == "2027-02-28",
              str(info_before.get("statusDate")))
        check("the source has a baseline on every task", all(t.get("baselineFinish") for t in before.values()))

        for fmt, ext, keeps_all in (("mspdi", "xml", True), ("mpx", "mpx", False)):
            print(f"\n{fmt}")
            target = work / f"out.{ext}"
            result = mcp.call("project_export", handle=h, path=str(target), format=fmt)
            notes = " ".join(result.get("notes", []))
            back = mcp.call("project_open", path=str(target))["handle"]
            after = state(mcp, back)
            info_after = mcp.call("project_info", handle=back)

            same_progress = all(
                abs((before[u].get("percentComplete") or 0) - (after.get(u, {}).get("percentComplete") or 0)) <= 1
                and (before[u].get("actualStart") or "")[:16] == (after.get(u, {}).get("actualStart") or "")[:16]
                and (before[u].get("actualFinish") or "")[:16] == (after.get(u, {}).get("actualFinish") or "")[:16]
                for u in before)
            same_baseline = all(
                (before[u].get("baselineStart") or "")[:16] == (after.get(u, {}).get("baselineStart") or "")[:16]
                and (before[u].get("baselineFinish") or "")[:16] == (after.get(u, {}).get("baselineFinish") or "")[:16]
                for u in before)
            same_status = (info_before.get("statusDate") or "")[:10] == (info_after.get("statusDate") or "")[:10]
            everything = same_progress and same_baseline and same_status

            if keeps_all:
                check(f"{fmt}: progress and actual dates survive, task by task", same_progress, str(summary(after)))
                check(f"{fmt}: baseline survives", same_baseline)
                check(f"{fmt}: status date survives", same_status,
                      f"{info_before.get('statusDate')} -> {info_after.get('statusDate')}")
                check(f"{fmt}: the export says it was verified", "Verified" in notes and "WARNING" not in notes, notes)
            else:
                # Whatever this format drops, the export must say so rather than report plain success.
                check(f"{fmt}: a loss is never reported as a clean success",
                      everything or "WARNING" in notes,
                      f"progress={same_progress} baseline={same_baseline} status={same_status}; notes: {notes}")
                if not everything:
                    check(f"{fmt}: the warning names what was lost",
                          any(f in notes for f in ("baseline", "percentComplete", "actual", "statusDate")), notes)

        print("\nproject_save with format='mpp' is honest about where it goes")
        health = mcp.call("project_health")
        if not health.get("capabilities", {}).get("write_native_mpp"):
            try:
                mcp.call("project_save", handle=h, op="save_as", path=str(work / "x.mpp"), format="mpp")
                check("without Microsoft Project, saving .mpp is refused", False, "it did not refuse")
            except RuntimeError as ex:
                check("without Microsoft Project, saving .mpp is refused with the reason", "MSPDI" in str(ex), str(ex)[:120])
            check("and nothing named .mpp or .xml was produced by it",
                  not (work / "x.mpp").exists() and not (work / "x.xml").exists())
        else:
            print("  (Microsoft Project is registered here; the native path is covered by project-engine-test.py)")
    finally:
        mcp.close()

    print()
    total = passed + len(failures)
    if failures:
        print(f"{len(failures)} of {total} checks failed:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print(f"=== {passed}/{total} checks passed ===")


if __name__ == "__main__":
    main()
