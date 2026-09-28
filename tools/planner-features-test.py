#!/usr/bin/env python3
"""What a planner had to leave the server for, and the readings that misled them.

Built from a field report on a real construction schedule, where each of these forced a switch to
driving Microsoft Project by hand:

  * the working week (half or full Saturdays), working exceptions, the project, task and resource
    calendars
  * task type, effort-driven, fixed cost, baseline cost without touching baseline dates, clearing an
    actual start
  * rates, overtime rates, material units, max units that Project would actually see
  * an outline built in one batch; children added under an earlier summary; 'outline' indents that
    left summaries reading as detail tasks
  * lags in elapsed days and in percent; lags marked as justified in the QA
  * a read-only handle must never write its file; an export must never overwrite an open schedule
  * what-if scenarios side by side; the S-curve as an Excel workbook with a chart; a forecast curve
  * a status date without a time is the whole day; a finished task under 100% is flagged

Runs on the internal engine and never starts Microsoft Project, so it is safe on any machine.

    python tools/planner-features-test.py
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_BIN = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0"
EXE = _BIN / ("horizun-msproject-mcp.exe" if sys.platform == "win32" else "horizun-msproject-mcp")

sys.path.insert(0, str(Path(__file__).resolve().parent))
FIXTURE = __import__("importlib").import_module("imported-file-test").FIXTURE  # the Project-saved base

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
                                "clientInfo": {"name": "planner-features-test", "version": "1"}})
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
            raise RuntimeError(f"{tool}: {r.get('error') or r['result']['content'][0]['text'][:400]}")
        return json.loads(r["result"]["content"][0]["text"])

    def close(self) -> None:
        self.p.stdin.close()
        self.p.terminate()


def rows(mcp: Mcp, h: str) -> dict[str, dict]:
    return {t["name"]: t for t in mcp.call("tasks_query", handle=h, limit=1000, sort="wbs")["items"]}


def ok(r: dict) -> bool:
    return r["applied"] > 0 and not r["rejected"]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-planner-"))
    base = work / "base.xml"
    base.write_text(FIXTURE, encoding="utf-8")
    mcp = Mcp()
    try:
        print("read-only never writes")
        before = digest(base)
        ro = mcp.call("project_open", path=str(base), mode="readonly")["handle"]
        mcp.call("schedule_qa", handle=ro)
        mcp.call("schedule_analyze", handle=ro)
        try:
            mcp.call("project_export", handle=ro, path=str(base), format="mspdi")
            check("an export over a schedule open read-only is refused", False, "it was written")
        except RuntimeError as ex:
            check("an export over a schedule open read-only is refused", "never writes over" in str(ex), str(ex)[:200])
        mcp.call("project_save", handle=ro, op="close")
        check("the file is byte for byte what it was", digest(base) == before)

        h = mcp.call("project_open", path=str(base))["handle"]

        print("\nthe working week")
        r = mcp.call("calendars_write", handle=h, ops=[
            {"op": "set_week", "name": "Obra L-S", "days": "sat", "hours": "08:00-13:00"}])
        check("Saturday mornings are set on the project calendar", ok(r), json.dumps(r["rejected"])[:300])
        info = mcp.call("project_info", handle=h)
        check("project_info shows the week as it now is",
              info.get("workingWeek", {}).get("Saturday") == "08:00-13:00"
              and info["workingWeek"].get("Monday") == "08:00-12:00,13:00-17:00"
              and info["workingWeek"].get("Sunday") == "", json.dumps(info.get("workingWeek")))
        check("and names the project calendar and currency", info.get("projectCalendar") == "Obra L-S",
              str(info.get("projectCalendar")))
        r = mcp.call("calendars_write", handle=h, ops=[
            {"op": "set_week", "name": "Obra L-S", "days": "lun-vie,dom", "hours": "07:00-99:00"}])
        check("malformed hours are refused, not guessed", r["applied"] == 0 and r["rejected"], json.dumps(r)[:200])
        r = mcp.call("calendars_write", handle=h, ops=[
            {"op": "create", "name": "Sábado completo", "basedOn": "Obra L-S"},
            {"op": "set_week", "name": "Sábado completo", "days": "sab", "hours": "08:00-12:00,13:00-17:00"},
            {"op": "set_exception", "name": "Obra L-S", "from": "2036-10-12", "working": True, "hours": "08:00-12:00"},
            {"op": "set_exception", "name": "Obra L-S", "from": "2036-12-25"}])
        check("a derived calendar, its week, and working and non-working exceptions", r["applied"] == 4,
              json.dumps(r["rejected"])[:300])
        info = mcp.call("project_info", handle=h)
        r = mcp.call("calendars_write", handle=h, ops=[{"op": "set_week", "name": "Sábado completo", "days": "sun",
                                                        "hours": "16:00-24:00"}])
        check("a night shift to 24:00 is accepted", ok(r), json.dumps(r["rejected"])[:200])
        t0 = mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Probe", "calendar": "Peón"}])
        check("a resource's own calendar is refused as a task calendar",
              any(x["field"] == "calendar" for x in t0["rejected"]), json.dumps(t0["rejected"])[:200])
        mcp.call("tasks_write", handle=h, ops=[{"op": "delete", "uid": rows(mcp, h)["Probe"]["uid"]}])
        try:
            mcp.call("schedule_analyze", handle=h, aspects=["lookahead"], ownerField="Responsable")
            check("a look-ahead owner field that is not a text field is refused", False, "accepted")
        except RuntimeError as ex:
            check("a look-ahead owner field that is not a text field is refused", "not a text field" in str(ex), str(ex)[:200])

        print("\ntasks, in one batch")
        r = mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Estructura", "key": "est"},
            {"op": "create", "name": "Piso 1", "key": "p1", "parentKey": "est"},
            {"op": "create", "name": "Columnas P1", "parentKey": "p1", "duration": "5d", "taskType": "FixedDuration",
             "effortDriven": False, "fixedCost": 1500, "fixedCostAccrual": "End", "calendar": "Sábado completo",
             "ignoreResourceCalendar": True},
            {"op": "create", "name": "Losa P1", "parentKey": "p1", "duration": "4d"},
            {"op": "create", "name": "Acabados", "key": "acb"},
            {"op": "create", "name": "Pintura", "parentKey": "acb", "duration": "3d"},
        ])
        check("an outline built in one call, with type, cost and calendar", ok(r) and r["applied"] == 6,
              json.dumps(r["rejected"])[:300])
        t = rows(mcp, h)
        # Added under an earlier summary after later rows exist: must land under it, not under the last row.
        r = mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Vigas P1", "parentUid": t["Piso 1"]["uid"], "duration": "2d"}])
        t = rows(mcp, h)
        check("a task added later under an earlier summary lands under it",
              t["Vigas P1"].get("wbs") == "1.1.3" and t["Vigas P1"].get("outlineLevel") == 3
              and t["Pintura"].get("wbs") == "2.1", str({n: t[n].get("wbs") for n in t}))
        check("the summaries read as summaries",
              all(t[n]["summary"] for n in ["Estructura", "Piso 1", "Acabados"]), str({n: t[n]["summary"] for n in t}))

        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Flat A"}, {"op": "create", "name": "Flat B"}])
        b = rows(mcp, h)["Flat B"]["uid"]
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "outline", "uid": b, "indent": 1}])
        t = rows(mcp, h)
        check("an 'outline' indent makes the row above a summary",
              ok(r) and t["Flat A"]["summary"] and t["Flat B"].get("outlineLevel") == 2, json.dumps(r["rejected"])[:200])

        col = t["Columnas P1"]["uid"]
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": col, "baselineCost": 25000, "baselineSlot": 1}])
        check("a baseline cost goes in on its own, into baseline 1", ok(r), json.dumps(r["rejected"])[:200])
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": col, "taskType": "FixedWhatever"}])
        check("an unknown task type is refused", r["applied"] == 0 and r["rejected"])
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": col, "actualStart": "2036-10-06T08:00:00",
                                                     "percentComplete": 20}])
        r2 = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": col, "actualStart": "none"}])
        t = rows(mcp, h)
        check("actualStart 'none' returns a task to not started",
              ok(r2) and not t["Columnas P1"].get("actualStart") and (t["Columnas P1"].get("percentComplete") or 0) == 0,
              json.dumps(r2["rejected"])[:200])

        print("\ndependencies")
        losa, vigas = t["Losa P1"]["uid"], t["Vigas P1"]["uid"]
        r = mcp.call("links_write", handle=h, ops=[
            {"op": "link", "from": col, "to": losa, "lag": "3ed"},
            {"op": "link", "from": col, "to": vigas, "type": "SS", "lag": "50%"}])
        check("lags in elapsed days and in percent", ok(r) and r["applied"] == 2, json.dumps(r["rejected"])[:300])
        qa = mcp.call("schedule_qa", handle=h, justifiedLags=[losa, vigas])
        lags = next(f for f in qa["findings"] if f["rule"] == "dcma_03_lags")
        check("lags marked as justified are not counted", losa not in lags.get("uids", []) and vigas not in lags.get("uids", [])
              and any("justified" in n for n in qa.get("notes", [])), json.dumps(lags)[:300])

        print("\nresources")
        r = mcp.call("resources_write", handle=h, ops=[
            {"op": "update", "uid": 3, "maxUnits": 300, "standardRate": 12.5, "overtimeRate": 18.75, "calendar": "Obra L-S"},
            {"op": "create", "name": "Concreto f'c=210", "type": "Material", "materialLabel": "m3", "standardRate": 420},
        ])
        check("rates, max units, calendar and a material with its unit", r["applied"] == 2 and not r["rejected"],
              json.dumps(r["rejected"])[:300])
        res = {x.get("name"): x for x in mcp.call("resources_query", handle=h)["items"]}
        peon = res["Peón"]
        check("resources_query shows them as Project will",
              peon.get("maxUnits") == 300 and peon.get("standardRate") == 12.5 and peon.get("overtimeRate") == 18.75
              and peon.get("calendar") == "Obra L-S" and res["Concreto f'c=210"].get("materialLabel") == "m3", json.dumps(peon))
        r = mcp.call("resources_write", handle=h, ops=[{"op": "update", "uid": 3, "materialLabel": "kg"}])
        check("a unit on a work resource is refused, by name",
              any(x["field"] == "materialLabel" for x in r["rejected"]), json.dumps(r)[:200])
        concrete = res["Concreto f'c=210"]["uid"]
        r = mcp.call("resources_write", handle=h, ops=[{"op": "assign", "uid": concrete, "taskUid": losa, "units": 12.5}])
        check("a material is assigned by quantity", ok(r), json.dumps(r["rejected"])[:200])

        print("\nsave and read back")
        out = work / "saved.xml"
        saved = mcp.call("project_save", handle=h, op="save_as", path=str(out), format="mspdi")
        notes = " ".join(saved.get("notes") or [])
        check("everything above survives a save, verified on reading back", "all preserved" in notes, notes[:400])
        xml = out.read_text(encoding="utf-8")
        check("the file carries Saturday 08:00-13:00 on 'Obra L-S'",
              re.search(r"<Name>Obra L-S</Name>.*?<DayType>7</DayType>\s*<DayWorking>1</DayWorking>\s*<WorkingTimes>\s*"
                        r"<WorkingTime>\s*<FromTime>08:00:00</FromTime>\s*<ToTime>13:00:00</ToTime>", xml, re.S) is not None)

        print("\nanalysis")
        sc = mcp.call("timephased_query", handle=h, measure="s_curve", granularity="week")
        pv = [b["period"] for b in sc["curves"]["pv"]]
        gaps = [a for a, b in zip(pv, pv[1:]) if (int(b[8:]) - int(a[8:])) % 7 not in (0,) and b[:7] == a[:7]]
        check("the S-curve has every week and a forecast curve", "forecast" in sc["curves"] and not gaps, str(pv[:6]))

        xlsx = work / "curva.xlsx"
        mcp.call("project_export", handle=h, path=str(xlsx), format="scurve_xlsx")
        with zipfile.ZipFile(xlsx) as z:
            names = z.namelist()
            chart = z.read("xl/charts/chart1.xml").decode("utf-8") if "xl/charts/chart1.xml" in names else ""
        check("the S-curve exports as a workbook with a native line chart",
              "xl/worksheets/sheet1.xml" in names and "<c:lineChart>" in chart, str(names))

        comparison = mcp.call("schedule_scenarios", handle=h, scenarios=[
            {"name": "A: sábado completo", "calendars": [
                {"op": "set_week", "name": "Obra L-S", "days": "sat", "hours": "08:00-12:00,13:00-17:00"}]},
            {"name": "B: sin sábados", "calendars": [{"op": "set_week", "name": "Obra L-S", "days": "sat", "hours": ""}]},
        ])
        a, b2 = comparison["scenarios"]
        check("what-if scenarios come back side by side, measured",
              a["finish"] and b2["finish"] and a["finish"] <= b2["finish"] and a["applied"] == 1,
              json.dumps(comparison)[:400])
        after = mcp.call("project_info", handle=h)["workingWeek"]["Saturday"]
        check("and nothing of them was committed", after == "08:00-13:00", after)

        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": t["Pintura"]["uid"],
                                                "actualStart": "2036-10-06T10:00:00", "percentComplete": 30}])
        qa = mcp.call("schedule_qa", handle=h, statusDate="2036-10-06")
        invalid = next(f for f in qa["findings"] if f["rule"] == "dcma_09_invalid_dates")
        check("work reported at 10:00 on the status date is not 'in the future'",
              invalid.get("evaluated") and t["Pintura"]["uid"] not in invalid.get("uids", []), json.dumps(invalid)[:300])
        check("the QA has the finished-but-not-100% rule",
              any(f["rule"] == "hrz_finished_incomplete" for f in qa["findings"]))

        rec = mcp.call("schedule_recovery", handle=h, targetFinish="2036-10-08")
        check("recovery either proposes options or says why each lever was dropped",
              rec["options"] or any(":" in n for n in rec.get("notes", [])), json.dumps(rec.get("notes"))[:400])
    finally:
        mcp.close()

    print(f"\n{passed} passed, {len(failures)} failed")
    if failures:
        sys.exit(1)


if __name__ == "__main__":
    main()
