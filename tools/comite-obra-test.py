#!/usr/bin/env python3
"""What the «Comité de obra» exercise found on 2026-10-01, held in place.

A structure schedule generated from three Revit models, taken through QA, a baseline, a progress cut,
recovery, scenarios and an export for Power BI, turned up six defects. Each has a check here:

1. schedule_generate started the structure of a level after the pipes of the level below, and let a
   sequence with the slab before its columns through without a word.
2. An actual finish after the planned one left the finish and the duration where they were.
3. schedule_recovery said nothing was behind beside an SPI of 0.818 on the same cut.
4. DCMA 01 counted the project's start and finish milestones as missing logic.
5. project_export wrote a file with no extension when the path had none.
6. Scenarios that add Saturdays showed a cost change of 0, premium unpriced and unmarked.

Runs on the internal engine and never starts Microsoft Project.

    python tools/comite-obra-test.py
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
                                "clientInfo": {"name": "comite-obra-test", "version": "1"}})
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


def chain(mcp: Mcp, path: Path, durations: list[str], start: str = "2026-10-05") -> tuple[str, list[int]]:
    """A start milestone, then tasks in a finish-to-start chain."""
    h = mcp.call("project_open", path=str(path), create=True, name=path.stem, startDate=start)["handle"]
    ops = [{"op": "create", "name": "Inicio", "duration": "0d"}] + [
        {"op": "create", "name": f"T{n}", "duration": d} for n, d in enumerate(durations, 1)]
    mcp.call("tasks_write", handle=h, ops=ops)
    uids = sorted(t["uid"] for t in rows(mcp, h).values() if not t["summary"])
    mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b} for a, b in zip(uids, uids[1:])])
    return h, uids


def building(levels: list[str], codes: dict[str, tuple[str, float]]) -> list[dict]:
    """Elements per level: code -> (category, m3)."""
    out = []
    for level in levels:
        for code, (category, qty) in codes.items():
            out.append({"elementId": f"{code}-{level}", "code": code, "category": category,
                        "level": f"Nivel {level}", "quantity": qty, "unit": "m3"})
    return out


def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-comite-"))
    mcp = Mcp()
    try:
        print("1. schedule_generate: a level stands on the structure below, not on its pipes")
        elements = building(["01", "02", "03"], {
            "EST-COL": ("Structural Columns", 8), "EST-VIG": ("Structural Framing", 8),
            "EST-LOS": ("Floors", 8), "MEP-TUB": ("Pipes", 80)})
        g = mcp.call("schedule_generate", outputPath=str(work / "gen.xml"), startDate="2026-10-05",
                     elements=elements, productivity={"m3": 8}, columnsWithLevelAbove=True)
        t = {x["name"]: x for x in rows(mcp, g["handle"]).values()}
        slab1 = t["Floors EST-LOS — Nivel 01"]
        pipe1 = t["Pipes MEP-TUB — Nivel 01"]
        col2 = t["Structural Columns EST-COL — Nivel 02"]
        check("the columns of level 02 start when slab 01 is done",
              col2["start"] >= slab1["finish"], f'{slab1["finish"]} -> {col2["start"]}')
        check("and do not wait for the pipes of level 01 (10 days of them)",
              col2["start"] < pipe1["finish"], f'pipes 01 finish {pipe1["finish"]}, columns 02 start {col2["start"]}')
        check("a constructible sequence raises no warning", not g.get("sequenceWarnings"), json.dumps(g.get("sequenceWarnings")))

        bad = mcp.call("schedule_generate", outputPath=str(work / "bad.xml"), startDate="2026-10-05",
                       elements=elements, productivity={"m3": 8}, sequence=["los", "col", "vig", "tub"])
        warnings = bad.get("sequenceWarnings") or []
        check("a slab sequenced before its columns is warned about",
              any("EST-LOS" in w and "EST-COL" in w for w in warnings), json.dumps(warnings)[:400])
        check("and the warning heads the notes", bad["notes"][0].startswith("WARNING"), bad["notes"][0][:200])
        late_pipes = mcp.call("schedule_generate", outputPath=str(work / "bad2.xml"), startDate="2026-10-05",
                              elements=elements, productivity={"m3": 8}, sequence=["tub", "col", "vig", "los"])
        check("installations sequenced before the structure are warned about",
              any("MEP-TUB" in w for w in late_pipes.get("sequenceWarnings") or []),
              json.dumps(late_pipes.get("sequenceWarnings")))
        walls = building(["01"], {"EST-CIM": ("Structural Foundations", 8), "EST-MUR": ("Muros de contención", 8),
                                  "EST-LOS": ("Floors", 8)})
        ok = mcp.call("schedule_generate", outputPath=str(work / "walls.xml"), startDate="2026-10-05",
                      elements=walls, productivity={"m3": 8}, sequence=["cim", "mur", "los"])
        check("a structural retaining wall before the ground slab is no warning",
              not ok.get("sequenceWarnings"), json.dumps(ok.get("sequenceWarnings")))

        print("\n2. tasks_write: an actual finish moves the finish and the duration, as in Project")
        h, u = chain(mcp, work / "actual.xml", ["10d", "5d"])
        t1, t2 = u[1], u[2]
        before = rows(mcp, h)
        check("planned: T1 ends before 2026-10-21", before[t1]["finish"] < "2026-10-21", before[t1]["finish"])
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t1, "percentComplete": 100,
                                                    "actualStart": "2026-10-05", "actualFinish": "2026-10-21"}])
        after = rows(mcp, h)
        check("the write is applied, nothing refused", r["applied"] == 1 and not r["rejected"], json.dumps(r["rejected"]))
        check("finish = actual finish, at the end of the working day",
              after[t1]["finish"] == "2026-10-21T17:00:00" and after[t1]["actualFinish"] == "2026-10-21T17:00:00",
              f'{after[t1]["finish"]} / {after[t1].get("actualFinish")}')
        check("duration = working time from actual start to actual finish (13 days)",
              after[t1]["duration"] == "13d", after[t1]["duration"])
        check("the successor starts the next working day", after[t2]["start"].startswith("2026-10-22"), after[t2]["start"])
        check("still 100%, all of it actual", after[t1]["percentComplete"] == 100, str(after[t1]["percentComplete"]))
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t1, "actualFinish": "2026-10-14T17:00"}])
        early = rows(mcp, h)
        check("an earlier actual finish shortens it (8 days)",
              early[t1]["duration"] == "8d" and early[t1]["finish"] == "2026-10-14T17:00:00",
              f'{early[t1]["duration"]} {early[t1]["finish"]}')
        try:
            r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t2, "actualStart": "2026-10-20",
                                                        "actualFinish": "2026-10-19"}])
            refused = any(x["field"] == "actualFinish" and "before" in x["reason"] for x in r["rejected"])
        except RuntimeError as ex:
            refused = "before" in str(ex)
        check("an actual finish before the actual start is refused", refused)

        print("\n3. schedule_recovery: late is measured against the baseline, as SPI is")
        h, u = chain(mcp, work / "recovery.xml", ["10d", "10d", "10d"])
        mcp.call("schedule_update", handle=h, op="save_baseline", reason="test")
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u[1], "actualStart": "2026-10-05", "percentComplete": 30}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2026-10-21")
        mcp.call("schedule_update", handle=h, op="reschedule_incomplete")
        ev = mcp.call("baseline_compare", handle=h)["project"]
        rec = mcp.call("schedule_recovery", handle=h)
        kinds = {x["uid"]: x["kind"] for x in rec["late"]}
        check("earned value reads behind (SPI < 1)", (ev.get("spi") or 1) < 1, json.dumps(ev))
        check("recovery reports the same SPI", rec.get("spi") == ev.get("spi"), f'{rec.get("spi")} vs {ev.get("spi")}')
        check("and finds work behind: T1 past its baseline finish, T2 past its baseline start",
              kinds.get(u[1]) == "overrunning" and kinds.get(u[2]) == "not_started", json.dumps(rec["late"])[:400])
        check("measured against the baseline, and says so", rec.get("measuredAgainst") == "baseline", str(rec.get("measuredAgainst")))
        check("no 'Nothing is behind' beside an SPI below 1",
              not any("Nothing is behind" in n for n in rec["notes"]), json.dumps(rec["notes"])[:300])
        h2, u2 = chain(mcp, work / "recovery2.xml", ["10d", "10d"])
        mcp.call("schedule_update", handle=h2, op="save_baseline", reason="test")
        mcp.call("tasks_write", handle=h2, ops=[{"op": "update", "uid": u2[1], "actualStart": "2026-10-05", "percentComplete": 20}])
        rec = mcp.call("schedule_recovery", handle=h2, statusDate="2026-10-12")
        behind = [x for x in rec["late"] if x["uid"] == u2[1]]
        check("work in progress with less done than planned is behind_plan",
              behind and behind[0]["kind"] == "behind_plan" and behind[0]["plannedPercent"] == 40,
              json.dumps(rec["late"])[:300])

        print("\n4. schedule_qa: DCMA 01 leaves out the project's start and finish milestones")
        h, u = chain(mcp, work / "qa.xml", ["5d", "5d", "0d"])
        qa = {f["rule"]: f for f in mcp.call("schedule_qa", handle=h)["findings"]}
        d1 = qa["dcma_01_logic"]
        check("a network closed by its start and finish milestones passes", d1["passed"] and not d1.get("uids"), json.dumps(d1))
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Suelta", "duration": "3d"}])
        qa = {f["rule"]: f for f in mcp.call("schedule_qa", handle=h)["findings"]}
        loose = [t["uid"] for t in rows(mcp, h).values() if t["name"] == "Suelta"]
        check("a task with no logic is still counted, and only it",
              qa["dcma_01_logic"].get("uids") == loose, json.dumps(qa["dcma_01_logic"]))

        print("\n5. project_export: a path with no extension gets the format's")
        for fmt, ext in [("pbip_dataset", ".json"), ("csv", ".csv"), ("json", ".json")]:
            target = work / f"export-{fmt}"
            r = mcp.call("project_export", handle=h, path=str(target), format=fmt)
            written = Path(r["path"])
            check(f"{fmt}: written as {ext}, and the reply says so",
                  written.suffix == ext and written.exists() and not target.exists()
                  and any("extension" in n for n in r.get("notes", [])), json.dumps(r))
        r = mcp.call("project_export", handle=h, path=str(work / "named.data.json"), format="pbip_dataset")
        check("a path with an extension is kept as given", r["path"].endswith("named.data.json"), r["path"])

        print("\n6. schedule_scenarios: Saturdays are priced, or marked not priced")
        h, u = chain(mcp, work / "sat.xml", ["10d", "10d"])
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t, "fixedCost": 1000} for t in u[1:]])
        sat = {"name": "sábados", "calendars": [{"op": "set_week", "name": "Standard", "days": "sat", "hours": "08:00-12:00,13:00-17:00"}]}
        other = {"name": "más corto", "tasks": [{"op": "update", "uid": u[2], "duration": "8d"}]}
        sc = mcp.call("schedule_scenarios", handle=h, scenarios=[sat, other])
        s, o = sc["scenarios"]
        check("Saturdays move the finish", (s["finishDeltaDays"] or 0) < 0, json.dumps(s))
        check("and the hours they add are counted", s["extraTimeHours"] > 0, json.dumps(s))
        check("with no rate, the scenario is marked NOT valued instead of costing 0 more",
              s["extraTimeValued"] is False and s.get("extraTimeCost") is None and "NOT VALUED" in (s.get("costNote") or ""),
              json.dumps(s))
        check("a scenario that adds no working time is valued as it is",
              o["extraTimeHours"] == 0 and o["extraTimeValued"] is True, json.dumps(o))
        priced = mcp.call("schedule_scenarios", handle=h, scenarios=[sat], extraTimeCostPerHour=50)["scenarios"][0]
        check("with a rate, the premium is priced and in the cost change",
              priced["extraTimeValued"] is True and priced["extraTimeCost"] == round(priced["extraTimeHours"] * 50, 2)
              and abs(priced["costDelta"] - priced["extraTimeCost"]) < 0.01, json.dumps(priced))
        check("16 working days over Saturdays: 3 Saturdays x 8 h on the open task(s)",
              priced["extraTimeHours"] == 24, str(priced["extraTimeHours"]))
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
