#!/usr/bin/env python3
"""What the end-to-end dry run of 2026-09-30 found, held in place.

A schedule generated from a Revit model and taken through QA, logic fixes, a calendar, resources,
baseline, progress, rescheduling, earned value, scenarios and risk turned up ten defects. Each has a
check here, on schedules built in this suite: links refused for a cycle through a link removed earlier,
a resource created on a file read from disk with no calendar, a baseline with a budget of 0, a start
milestone dragged past work already done, a CPI of 1 nobody measured, scenarios costing 0, DCMA 11 and
13 measured against the wrong things, a health note contradicting its own matrix, day figures in mixed
units, a Monte Carlo biased against the schedule it simulates — and columns scheduled before the slab
that carries them.

Runs on the internal engine and never starts Microsoft Project; the parts that need Project itself
(resource calendars in the hand-off, the split reschedule) are in project-engine-test.py.

    python tools/field-report-test.py
"""

from __future__ import annotations

import json
import os
import re
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
                                "clientInfo": {"name": "field-report-test", "version": "1"}})
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


def rows(mcp: Mcp, h: str) -> dict:
    return {t["uid"]: t for t in mcp.call("tasks_query", handle=h, limit=1000)["items"]}


def chain(mcp: Mcp, path: Path, durations: list[str], start: str = "2026-10-05", milestone: bool = True) -> tuple[str, list[int]]:
    """A start milestone, then tasks in a finish-to-start chain."""
    h = mcp.call("project_open", path=str(path), create=True, name=path.stem, startDate=start)["handle"]
    ops = ([{"op": "create", "name": "Inicio", "duration": "0d"}] if milestone else []) + [
        {"op": "create", "name": f"T{n}", "duration": d} for n, d in enumerate(durations, 1)]
    mcp.call("tasks_write", handle=h, ops=ops)
    uids = sorted(t["uid"] for t in rows(mcp, h).values() if not t["summary"])
    mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b} for a, b in zip(uids, uids[1:])])
    return h, uids


def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-field-"))
    mcp = Mcp()
    try:
        print("1. links_write: a cycle through a link removed earlier is no cycle")
        h, u = chain(mcp, work / "links.xml", ["5d", "5d", "5d"], milestone=False)
        a, b, c = u
        batch = [{"op": "unlink", "from": a, "to": b}, {"op": "link", "from": c, "to": a, "lag": "3ed"}]
        dry = mcp.call("links_write", handle=h, ops=batch, dryRun=True)
        check("an unlink earlier in the batch opens the way for the link after it (dry run)",
              dry["applied"] == 2 and not dry["rejected"], json.dumps(dry["rejected"])[:300])
        h2, u2 = chain(mcp, work / "links2.xml", ["5d", "5d", "5d"], milestone=False)
        a2, b2, c2 = u2
        mcp.call("links_write", handle=h2, ops=[{"op": "unlink", "from": a2, "to": b2}])
        dry = mcp.call("links_write", handle=h2, ops=[{"op": "link", "from": c2, "to": a2}], dryRun=True)
        real = mcp.call("links_write", handle=h2, ops=[{"op": "link", "from": c2, "to": a2}])
        check("after an applied unlink, the real apply accepts what the dry run accepted",
              dry["applied"] == 1 and real["applied"] == 1, json.dumps(real["rejected"])[:300])
        try:
            sure = mcp.call("links_write", handle=h2, ops=[{"op": "link", "from": a2, "to": c2}], dryRun=True)
            check("a real cycle is still refused", sure["applied"] == 0 and "cycle" in json.dumps(sure["rejected"]))
        except RuntimeError as ex:
            check("a real cycle is still refused", "cycle" in str(ex), str(ex)[:200])

        print("\n2. resources_write on a file read from disk: the resource works the project calendar")
        h, u = chain(mcp, work / "res.xml", ["5d", "3d"])
        mcp.call("calendars_write", handle=h, ops=[{"op": "set_week", "name": "Standard", "days": "sat", "hours": "08:00-12:00"}])
        mcp.call("project_save", handle=h)
        h = mcp.call("project_open", path=str(work / "res.xml"))["handle"]
        mcp.call("resources_write", handle=h, ops=[{"op": "create", "name": "Cuadrilla", "standardRate": 100}])
        crew = mcp.call("resources_query", handle=h)["items"][0]
        check("a new resource's calendar derives from the project calendar", crew.get("calendar") == "Standard", json.dumps(crew))
        r = mcp.call("resources_write", handle=h, ops=[{"op": "update", "uid": crew["uid"], "calendar": "Standard"}])
        check("'update calendar' on a resource survives the write", r["applied"] == 1 and not r["rejected"],
              json.dumps(r["rejected"]))
        mcp.call("resources_write", handle=h, ops=[{"op": "assign", "uid": crew["uid"], "taskUid": t} for t in u[1:]])
        mcp.call("project_save", handle=h)
        xml = (work / "res.xml").read_text(encoding="utf-8")
        resource = re.search(r"<Resource>\s*<UID>1</UID>.*?</Resource>", xml, re.S).group(0)
        cal_uid = re.search(r"<CalendarUID>(\d+)</CalendarUID>", resource)
        calendar = cal_uid and re.search(rf"<Calendar>\s*<UID>{cal_uid.group(1)}</UID>.*?</Calendar>", xml, re.S)
        project_cal = re.search(r"<CalendarUID>(\d+)</CalendarUID>", xml).group(1)
        check("the saved file links the resource to its calendar, based on the project's",
              calendar is not None and f"<BaseCalendarUID>{project_cal}</BaseCalendarUID>" in calendar.group(0),
              resource[:400])

        print("\n3/5/6. fixed costs: baseline budget, earned value with no actuals, scenarios")
        h, u = chain(mcp, work / "cost.xml", ["10d", "10d", "10d"])
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t, "fixedCost": 1000 * (i + 1)} for i, t in enumerate(u[1:])])
        mcp.call("schedule_update", handle=h, op="save_baseline", reason="test")
        got = rows(mcp, h)
        check("save_baseline stores the fixed cost as the baseline cost",
              [got[t].get("baselineCost") for t in u[1:]] == [1000, 2000, 3000], str([got[t].get("baselineCost") for t in u[1:]]))
        log = mcp.call("schedule_analyze", handle=h, aspects=["change_log"])["changeLog"]
        check("the change log records the budget the baseline holds",
              any(c["kind"] == "baseline" and c.get("budgetAfter") == 6000 for c in log), json.dumps(log)[:400])
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u[1], "actualStart": "2026-10-05", "percentComplete": 60}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2026-10-16")
        ev = mcp.call("baseline_compare", handle=h)["project"]
        check("with no actual cost recorded, AC and CPI are not computable (null), not EV and 1",
              ev.get("measure") == "cost" and ev.get("acwp") is None and ev.get("cpi") is None and ev.get("bcwp", 0) > 0
              and ev.get("actualsRecorded") is False,
              json.dumps(ev))
        status = mcp.call("schedule_analyze", handle=h, aspects=["status_report"])["statusReport"]
        check("baseline_compare and status_report agree that CPI cannot be read",
              status.get("cpi") is None and ev.get("cpi") is None, json.dumps(status)[:300])
        sc = mcp.call("schedule_scenarios", handle=h, scenarios=[
            {"name": "más corto", "tasks": [{"op": "update", "uid": u[3], "duration": "5d"}]}])
        check("scenarios cost the fixed costs the tasks carry",
              sc["current"]["cost"] == 6000 and sc["scenarios"][0]["cost"] == 6000, json.dumps(sc["current"]))

        print("\n4. reschedule_incomplete")
        h, u = chain(mcp, work / "resched.xml", ["10d", "5d"])
        mcp.call("schedule_update", handle=h, op="save_baseline", reason="test")
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u[1], "actualStart": "2026-10-05", "percentComplete": 30}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2026-10-20")
        before = rows(mcp, h)
        r = mcp.call("schedule_update", handle=h, op="reschedule_incomplete")
        after = rows(mcp, h)
        check("the task in progress has its remaining work moved past the status date",
              after[u[1]]["finish"] > before[u[1]]["finish"] and after[u[1]]["finish"] >= "2026-10-27",
              f'{before[u[1]]["finish"]} -> {after[u[1]]["finish"]}')
        check("the unstarted start milestone, before work already started, is left where it is",
              after[u[0]]["start"] == before[u[0]]["start"] and not after[u[0]].get("constraintType", "").startswith("StartNo"),
              json.dumps(after[u[0]]))
        check("and is named, with what to do", "Inicio" in json.dumps(r["rejected"]) and "complete" in json.dumps(r["rejected"]),
              json.dumps(r["rejected"])[:300])
        check("no negative float is created", r["impact"]["newNegativeFloat"] == 0, json.dumps(r["impact"]))

        print("\n7. schedule_qa: DCMA 11 and 13")
        h, u = chain(mcp, work / "qa.xml", ["5d", "5d", "5d", "5d"])
        mcp.call("schedule_update", handle=h, op="save_baseline", reason="test")
        # The first task slips; three are still in the future at the status date.
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u[1], "duration": "8d"}])
        qa = {f["rule"]: f for f in mcp.call("schedule_qa", handle=h, statusDate="2026-10-12")["findings"]}
        d11 = qa["dcma_11_missed_tasks"]
        check("DCMA 11 counts only tasks baselined to finish by the status date",
              d11.get("evaluated") is not False and d11.get("uids") == [u[1]] and "of 2" in d11.get("summary", "")
              or (d11.get("uids") == [u[1]]), json.dumps(d11))
        d13 = qa["dcma_13_cpli"]
        check("DCMA 13 measures against the baseline finish, not the file header", "baseline finish" in d13.get("summary", ""),
              json.dumps(d13))
        target = mcp.call("schedule_qa", handle=h, statusDate="2026-10-12", targetFinish="2026-12-31")["findings"]
        check("a target finish passed in is the one CPLI uses",
              any(f["rule"] == "dcma_13_cpli" and "2026-12-31" in f["summary"] and f["passed"] for f in target))
        h2, _ = chain(mcp, work / "qa2.xml", ["5d"])
        d13 = next(f for f in mcp.call("schedule_qa", handle=h2)["findings"] if f["rule"] == "dcma_13_cpli")
        check("with no target set anywhere, CPLI is not evaluated rather than measured against the header",
              d13.get("evaluated") is False, json.dumps(d13))

        print("\n8. project_health: notes agree with the matrix")
        health = mcp.call("project_health")
        caps, notes = health["capabilities"], " ".join(health["notes"])
        both_true = caps.get("write_native_mpp") and caps.get("level_resources")
        check("no note calls a capability false that the matrix reports true",
              not (both_true and "false here" in notes)
              and not (caps.get("write_native_mpp") and "writing the native binary .mpp (only" in notes)
              and not (caps.get("level_resources") and "resource levelling (its heuristic" in notes), notes[:400])

        print("\n9. day units: schedule shifts are working days, and say so")
        h, u = chain(mcp, work / "units.xml", ["5d", "5d"], start="2026-10-05")
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u[1], "duration": "10d"}])
        check("a 5-day extension moves the finish 5 working days, not 7 calendar days",
              abs(r["impact"]["projectFinishDeltaDays"] - 5) < 0.01, json.dumps(r["impact"]))
        check("the impact names its unit", "working days" in r["impact"].get("projectFinishDeltaUnit", ""), json.dumps(r["impact"]))
        rec = mcp.call("schedule_recovery", handle=h, targetFinish="2026-10-16")
        check("recovery names its unit too", "working days" in rec.get("dayUnit", ""), str(rec.get("dayUnit")))

        print("\n10. schedule_risk: the most-likely run is the schedule's own finish")
        h, u = chain(mcp, work / "risk.xml", ["6d", "9d", "7d", "12d"])
        mcp.call("calendars_write", handle=h, ops=[{"op": "set_week", "name": "Standard", "days": "sat", "hours": "08:00-12:00"}])
        mcp.call("links_write", handle=h, ops=[{"op": "unlink", "from": u[2], "to": u[3]},
                                               {"op": "link", "from": u[2], "to": u[3], "lag": "3ed"}])
        mcp.call("schedule_update", handle=h, op="recalculate")
        risk = mcp.call("schedule_risk", handle=h, iterations=500, seed=7, optimisticPct=0, pessimisticPct=0)
        check("with no spread, every percentile is the schedule's finish (Saturday half days, an elapsed lag)",
              risk["deterministicFinish"] == risk["scheduledFinish"][:10]
              and set(risk["percentiles"].values()) == {risk["scheduledFinish"][:10]},
              json.dumps({k: risk[k] for k in ("deterministicFinish", "scheduledFinish", "percentiles", "notes")}))

        print("\nschedule_generate: columns with the level they carry")
        elements = []
        for level in ["01", "02", "03"]:
            elements += [{"elementId": f"c{level}", "code": "COL", "category": "Structural Columns", "level": f"Nivel {level}", "quantity": 8, "unit": "m3"},
                         {"elementId": f"s{level}", "code": "LOS", "category": "Floors", "level": f"Nivel {level}", "quantity": 30, "unit": "m3"}]
        elements += [{"elementId": f"v{level}", "code": "VIG", "category": "Structural Framing", "level": f"Nivel {level}", "quantity": 12, "unit": "m3"}
                     for level in ["02", "03"]]
        g = mcp.call("schedule_generate", outputPath=str(work / "gen.xml"), startDate="2026-10-05", elements=elements,
                     productivity={"m3": 8}, sequence=["col", "vig", "los"])
        check("as modelled, the report warns that columns carry their base level",
              any("BASE level" in n for n in g["notes"]), json.dumps(g["notes"])[:300])
        g = mcp.call("schedule_generate", outputPath=str(work / "gen2.xml"), startDate="2026-10-05", elements=elements,
                     productivity={"m3": 8}, sequence=["col", "vig", "los"], columnsWithLevelAbove=True)
        t = {x["name"]: x for x in rows(mcp, g["handle"]).values()}
        slab1, col2, beam2, slab2 = (t["Floors LOS — Nivel 01"], t["Structural Columns COL — Nivel 02"],
                                     t["Structural Framing VIG — Nivel 02"], t["Floors LOS — Nivel 02"])
        check("columnsWithLevelAbove: slab 01, then the columns it carries, then beams and slab 02",
              slab1["finish"] <= col2["start"] and col2["finish"] <= beam2["start"] and beam2["finish"] <= slab2["start"],
              f'{slab1["finish"]} {col2["start"]} {beam2["start"]} {slab2["start"]}')
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
