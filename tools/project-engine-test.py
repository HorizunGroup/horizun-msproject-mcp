#!/usr/bin/env python3
"""Microsoft Project as the scheduling engine: exactness and safety.

Every date this server reports on a machine with Microsoft Project is supposed to be Project's own.
This suite holds it to that, with Project itself as the oracle:

  * a schedule built from nothing through this server is recalculated, saved as a native .mpp,
    and the same file recalculated by Project directly — the dates must agree on every task;
  * edits (a duration, a percent complete, a new link) made through this server and made by hand
    in Project on the same .mpp must leave the same dates and the same progress on every task;
  * the server must be a safe guest in a Project the user already has open: their unsaved document
    stays open, unchanged and active, the window stays visible, and nothing of ours is left behind;
  * an instance the server started itself must be gone when it is done.

It needs Windows and an installed Microsoft Project, and skips (exit 0) without them. No client data
is used: every schedule is generated here.

    python tools/project-engine-test.py
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_BIN = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0"
EXE = _BIN / ("horizun-msproject-mcp.exe" if sys.platform == "win32" else "horizun-msproject-mcp")
NS = "{http://schemas.microsoft.com/project}"

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
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        self.i = 0
        self.req("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                "clientInfo": {"name": "project-engine-test", "version": "1"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, m: dict) -> None:
        self.p.stdin.write(json.dumps(m) + "\n")
        self.p.stdin.flush()

    def req(self, method: str, params: dict) -> dict:
        self.i += 1
        self._send({"jsonrpc": "2.0", "id": self.i, "method": method, "params": params})
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


def winproj_pids() -> list[str]:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq WINPROJ.EXE", "/NH"],
                         capture_output=True, text=True).stdout
    return [line.split()[1] for line in out.splitlines() if "WINPROJ" in line]


def project():
    import win32com.client
    app = win32com.client.DispatchEx("MSProject.Application")
    app.Visible = False
    app.DisplayAlerts = False
    return app


def project_native(mpp: Path, out_xml: Path, edit=None) -> None:
    """The oracle: Project opens its own .mpp, optionally applies an edit by hand, recalculates."""
    app = project()
    try:
        app.FileOpenEx(str(mpp), True)
        if edit:
            edit(app.ActiveProject)
        app.CalculateProject()
        out_xml.unlink(missing_ok=True)
        # Positional on purpose: with named arguments Project writes a binary file under .xml.
        app.FileSaveAs(str(out_xml), 0, False, False, False, False, "", "", "", "MSProject.XML")
        app.FileCloseEx(0)
    finally:
        app.Quit(0)


def tasks(xml: Path) -> dict[str, dict[str, str]]:
    fields = ("Name", "Start", "Finish", "Summary", "PercentComplete", "ActualDuration", "RemainingDuration")
    out = {}
    for t in ET.parse(xml).getroot().iter(NS + "Task"):
        uid = t.findtext(NS + "UID") or ""
        if uid in ("", "0"):
            continue
        out[uid] = {f: (t.findtext(NS + f) or "") for f in fields}
    return out


def _norm(value: str) -> str:
    # MPXJ leaves a zero out where Project writes it: "" and "PT0H0M0S" (or "0") say the same thing.
    return "" if value in ("PT0H0M0S", "0") else value[:16]


def agree(a: dict, b: dict, fields=("Start", "Finish")) -> tuple[int, int, list[str]]:
    common = [u for u in a if u in b]
    bad = [u for u in common if any(_norm(a[u][f]) != _norm(b[u][f]) for f in fields)]
    return len(common) - len(bad), len(common), bad


def build(mcp: Mcp, work: Path) -> tuple[str, Path]:
    """A small site schedule exercising what differs between engines: FS/SS/FF, lag, a milestone,
    a constraint and a holiday."""
    h = mcp.call("project_open", path=str(work / "authored.xml"), create=True,
                 name="Engine test", startDate="2026-03-02")["handle"]
    mcp.call("calendars_write", handle=h, ops=[{"op": "set_exception", "from": "2026-03-23", "working": False}])
    names = ["Excavación", "Cimentación", "Estructura", "Muros", "Cubierta", "Instalaciones", "Acabados", "Entrega"]
    durations = ["5d", "8d", "15d", "10d", "6d", "12d", "9d", "0d"]
    mcp.call("tasks_write", handle=h, ops=[
        {"op": "create", "name": name, "duration": dur} for name, dur in zip(names, durations)])
    listing = mcp.call("tasks_query", handle=h, limit=100)
    uid = {t["name"]: t["uid"] for t in listing["items"]}
    mcp.call("links_write", handle=h, ops=[
        {"op": "link", "from": uid["Excavación"], "to": uid["Cimentación"], "type": "FS"},
        {"op": "link", "from": uid["Cimentación"], "to": uid["Estructura"], "type": "FS", "lag": "2d"},
        {"op": "link", "from": uid["Estructura"], "to": uid["Muros"], "type": "SS", "lag": "5d"},
        {"op": "link", "from": uid["Estructura"], "to": uid["Cubierta"], "type": "FS"},
        {"op": "link", "from": uid["Muros"], "to": uid["Instalaciones"], "type": "FF", "lag": "3d"},
        {"op": "link", "from": uid["Cubierta"], "to": uid["Acabados"], "type": "FS"},
        {"op": "link", "from": uid["Instalaciones"], "to": uid["Acabados"], "type": "FS"},
        {"op": "link", "from": uid["Acabados"], "to": uid["Entrega"], "type": "FS"},
    ])
    mcp.call("tasks_write", handle=h, ops=[
        {"op": "update", "uid": uid["Instalaciones"], "constraintType": "StartNoEarlierThan",
         "constraintDate": "2026-04-20"},
        {"op": "update", "uid": uid["Excavación"], "percentComplete": 100},
        {"op": "update", "uid": uid["Cimentación"], "percentComplete": 40},
    ])
    return h, uid


def main() -> None:
    if sys.platform != "win32":
        print("skipped: Microsoft Project only exists on Windows")
        return
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")

    mcp = Mcp()
    try:
        engine = mcp.call("project_health").get("schedulingEngine")
    finally:
        mcp.close()
    if engine != "microsoft-project":
        print(f"skipped: the scheduling engine here is {engine!r}, not Microsoft Project")
        return

    subprocess.run(["taskkill", "/F", "/IM", "WINPROJ.EXE"], capture_output=True)
    work = Path(tempfile.mkdtemp(prefix="hzpm-engine-"))
    print(f"engine: {engine}   scratch: {work}")

    # 1. A schedule authored from nothing: our recalculation vs Project recalculating our .mpp.
    print("\nauthored schedule")
    mcp = Mcp()
    try:
        h, uid = build(mcp, work)
        r = mcp.call("schedule_update", handle=h, op="recalculate")
        check("recalculate says Project computed the dates",
              any("Microsoft Project itself" in n for n in r.get("notes", [])), str(r.get("notes")))
        mcp.call("project_save", handle=h, op="save_as", path=str(work / "authored.mpp"), format="mpp")
        mcp.call("project_export", handle=h, path=str(work / "mcp.xml"), format="mspdi")
    finally:
        mcp.close()
    check("no instance of Project left running after the server is done", winproj_pids() == [],
          str(winproj_pids()))
    project_native(work / "authored.mpp", work / "oracle.xml")
    same, total, bad = agree(tasks(work / "mcp.xml"), tasks(work / "oracle.xml"))
    check("dates match Project on every task of an authored schedule", same == total and total > 0,
          f"{same}/{total}, differ: {bad}")

    # 2. Edits through the server vs the same edits made by hand in Project.
    print("\nedits against Project doing the same by hand")
    edits = [
        ("duration of an unstarted task", "tasks_write",
         [{"op": "update", "uid": uid["Estructura"], "duration": "20d"}],
         lambda pj: setattr(pj.Tasks.UniqueID(uid["Estructura"]), "Duration", 20 * 480)),
        ("duration of a task in progress", "tasks_write",
         [{"op": "update", "uid": uid["Cimentación"], "duration": "12d"}],
         lambda pj: setattr(pj.Tasks.UniqueID(uid["Cimentación"]), "Duration", 12 * 480)),
        ("percent complete", "tasks_write",
         [{"op": "update", "uid": uid["Estructura"], "percentComplete": 35}],
         lambda pj: setattr(pj.Tasks.UniqueID(uid["Estructura"]), "PercentComplete", 35)),
        ("a new link with lag", "links_write",
         [{"op": "link", "from": uid["Muros"], "to": uid["Cubierta"], "type": "FS", "lag": "4d"}],
         lambda pj: pj.Tasks.UniqueID(uid["Cubierta"]).TaskDependencies.Add(
             pj.Tasks.UniqueID(uid["Muros"]), 1, 4 * 480)),
    ]
    for label, tool, ops, by_hand in edits:
        mcp = Mcp()
        try:
            h = mcp.call("project_open", path=str(work / "authored.mpp"))["handle"]
            result = mcp.call(tool, handle=h, ops=ops)
            mcp.call("project_export", handle=h, path=str(work / "edit-mcp.xml"), format="mspdi")
        finally:
            mcp.close()
        project_native(work / "authored.mpp", work / "edit-oracle.xml", by_hand)
        a, b = tasks(work / "edit-mcp.xml"), tasks(work / "edit-oracle.xml")
        same, total, bad = agree(a, b)
        check(f"{label}: applied and verified", result.get("applied") == 1, json.dumps(result.get("rejected")))
        check(f"{label}: dates match Project on every task", same == total, f"{same}/{total}, differ: {bad}")
        same, total, bad = agree(a, b, ("PercentComplete", "ActualDuration", "RemainingDuration"))
        check(f"{label}: progress matches Project, summaries included", same == total, f"differ: {bad}")

    # 2b. MSPDI -> native .mpp keeps progress, baseline and status date. Found in 1.2.1: a 215-task
    # schedule with 41 finished and 2 in progress came back with 1 finished and none in progress.
    print("\nMSPDI to native .mpp keeps progress, baseline and status date")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(work / "progress.xml"), create=True,
                     name="Progress", startDate="2026-11-02")["handle"]
        mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": f"Actividad {n:02}", "duration": f"{2 + n % 4}d"} for n in range(30)])
        ids = sorted(t["uid"] for t in mcp.call("tasks_query", handle=h, limit=100)["items"] if not t["summary"])
        mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b} for a, b in zip(ids, ids[1:])])
        mcp.call("schedule_update", handle=h, op="save_baseline")
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": u, "percentComplete": 100} for u in ids[:10]]
                 + [{"op": "update", "uid": ids[10], "percentComplete": 60},
                    {"op": "update", "uid": ids[11], "percentComplete": 25}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2027-02-28")
        mcp.call("project_export", handle=h, path=str(work / "progress.xml"), format="mspdi")
        source = mcp.call("project_open", path=str(work / "progress.xml"))["handle"]
        result = mcp.call("project_export", handle=source, path=str(work / "progress.mpp"), format="mpp")
        back = mcp.call("project_open", path=str(work / "progress.mpp"))["handle"]
        want = {t["uid"]: t for t in mcp.call("tasks_query", handle=source, limit=100)["items"] if not t["summary"]}
        got = {t["uid"]: t for t in mcp.call("tasks_query", handle=back, limit=100)["items"] if not t["summary"]}
        info = mcp.call("project_info", handle=back)
    finally:
        mcp.close()
    check("the .mpp export says it was verified", any("Verified" in n for n in result.get("notes", [])),
          json.dumps(result.get("notes")))
    lost = [u for u in want if abs((want[u].get("percentComplete") or 0) - (got.get(u, {}).get("percentComplete") or 0)) > 1
            or (want[u].get("actualStart") or "")[:16] != (got.get(u, {}).get("actualStart") or "")[:16]
            or (want[u].get("actualFinish") or "")[:16] != (got.get(u, {}).get("actualFinish") or "")[:16]]
    check("every task keeps its progress and actual dates", not lost, f"lost on {lost[:8]}")
    check("the baseline survives",
          all((want[u].get("baselineFinish") or "")[:16] == (got.get(u, {}).get("baselineFinish") or "")[:16] for u in want))
    check("the status date survives", (info.get("statusDate") or "")[:10] == "2027-02-28", str(info.get("statusDate")))

    # 2c. Building on a schedule and writing it to .mpp: outline, Unicode resources, crews, materials,
    # calendars, levelling. Found in 1.3.0 on a real schedule: tasks one level too high, WBS "0",
    # 'Peón' written as 'Electricista', 15 bags of cement read back as 24, and no resource levelable.
    print("\nbuilding on a schedule and writing it to .mpp")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(work / "obra.xml"), create=True, name="Obra",
                     startDate="2026-11-02")["handle"]
        mcp.call("calendars_write", handle=h, ops=[
            {"op": "create", "name": "Obra L-S"}, {"op": "set_project_calendar", "name": "Obra L-S"},
            {"op": "set_week", "name": "Obra L-S", "days": "sat", "hours": "08:00-13:00"}])
        mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Preliminares", "key": "pre"},
            {"op": "create", "name": "Obras provisionales", "key": "op", "parentKey": "pre"},
            {"op": "create", "name": "Cerco", "parentKey": "op", "duration": "3d"},
            {"op": "create", "name": "Movimiento de tierras", "key": "mt"},
            {"op": "create", "name": "Excavación", "parentKey": "mt", "duration": "5d"},
            {"op": "create", "name": "Zanjas", "parentKey": "mt", "duration": "5d"}])
        rows = {t["name"]: t for t in mcp.call("tasks_query", handle=h, limit=100)["items"]}
        # Added under an earlier summary after later rows exist.
        mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Caseta", "parentUid": rows["Obras provisionales"]["uid"], "duration": "2d"}])
        mcp.call("resources_write", handle=h, ops=[
            {"op": "create", "name": "Peón", "maxUnits": 800}, {"op": "create", "name": "Electricista", "maxUnits": 200},
            {"op": "create", "name": "Cemento", "type": "Material", "materialLabel": "bls", "standardRate": 28}])
        res = {r.get("name"): r["uid"] for r in mcp.call("resources_query", handle=h)["items"]}
        rows = {t["name"]: t for t in mcp.call("tasks_query", handle=h, limit=100)["items"]}
        mcp.call("resources_write", handle=h, ops=[
            {"op": "assign", "uid": res["Peón"], "taskUid": rows["Caseta"]["uid"], "units": 400},
            {"op": "assign", "uid": res["Cemento"], "taskUid": rows["Caseta"]["uid"], "units": 15},
            {"op": "assign", "uid": res["Electricista"], "taskUid": rows["Excavación"]["uid"], "units": 200},
            {"op": "assign", "uid": res["Electricista"], "taskUid": rows["Zanjas"]["uid"], "units": 200}])
        level = mcp.call("schedule_update", handle=h, op="level_resources")
        rows = {t["name"]: t for t in mcp.call("tasks_query", handle=h, limit=100)["items"]}
        exported = mcp.call("project_export", handle=h, path=str(work / "obra.mpp"), format="mpp")
        back = mcp.call("project_open", path=str(work / "obra.mpp"), mode="readonly")["handle"]
        got = {t["name"]: t for t in mcp.call("tasks_query", handle=back, limit=100)["items"]}
        people = {r.get("name"): r for r in mcp.call("resources_query", handle=back, includeAssignments=True)["items"]}
        week = mcp.call("project_info", handle=back)["workingWeek"]
    finally:
        mcp.close()
    check("levelling runs in Project and separates the two tasks on one crew",
          level["applied"] == 1 and (rows["Excavación"]["finish"] <= rows["Zanjas"]["start"]
                                     or rows["Zanjas"]["finish"] <= rows["Excavación"]["start"]),
          f'{rows["Excavación"]["start"]}..{rows["Excavación"]["finish"]} / {rows["Zanjas"]["start"]}..{rows["Zanjas"]["finish"]}')
    check("the .mpp export is verified", any("Verified" in n for n in exported.get("notes", [])), json.dumps(exported)[:300])
    check("outline levels and WBS come back as built",
          {n: (got[n].get("outlineLevel"), got[n].get("wbs")) for n in ["Preliminares", "Obras provisionales", "Cerco", "Caseta",
                                                                          "Movimiento de tierras", "Zanjas"]}
          == {"Preliminares": (1, "1"), "Obras provisionales": (2, "1.1"), "Cerco": (3, "1.1.1"), "Caseta": (3, "1.1.2"),
              "Movimiento de tierras": (1, "2"), "Zanjas": (2, "2.2")},
          json.dumps({n: (t.get("outlineLevel"), t.get("wbs")) for n, t in got.items()}, ensure_ascii=False))
    on_caseta = {r: next((a["units"] for a in people[r].get("assignments") or [] if a["taskName"] == "Caseta"), None)
                 for r in ["Peón", "Electricista", "Cemento"]}
    check("'Peón' keeps its work at 400%, nothing lands on 'Electricista', cement is 15 bags",
          on_caseta == {"Peón": 400, "Electricista": None, "Cemento": 15}, json.dumps(on_caseta, ensure_ascii=False))
    check("Saturday 08:00-13:00 on the project calendar", week.get("Saturday") == "08:00-13:00", json.dumps(week))

    # 2d. A resource created on a schedule read from disk. Found in the 2026-09-30 dry run: it went to
    # Project with no calendar, Project gave it the locale's (9-13, 15-19), and assigning one crew cut a
    # quarter-day off 13 tasks, put finishes at 19:00 and collapsed the critical path to 3 tasks.
    print("\nassigning a resource created on a schedule read from disk")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(work / "crew.xml"), create=True, name="Crew", startDate="2026-10-05")["handle"]
        mcp.call("calendars_write", handle=h, ops=[{"op": "set_week", "name": "Standard", "days": "sat", "hours": "08:00-12:00"}])
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": f"Tarea {n}", "duration": d}
                                               for n, d in enumerate(["22d", "13d", "5.5d", "17.5d"], 1)])
        ids = sorted(t["uid"] for t in mcp.call("tasks_query", handle=h, limit=100)["items"] if not t["summary"])
        mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b} for a, b in zip(ids, ids[1:])])
        mcp.call("schedule_update", handle=h, op="recalculate")
        mcp.call("project_save", handle=h)
        h = mcp.call("project_open", path=str(work / "crew.xml"))["handle"]
        before = {t["uid"]: t for t in mcp.call("tasks_query", handle=h, limit=100)["items"]}
        mcp.call("resources_write", handle=h, ops=[{"op": "create", "name": "Cuadrilla concreto", "standardRate": 150000}])
        crew = mcp.call("resources_query", handle=h)["items"][0]["uid"]
        assigned = mcp.call("resources_write", handle=h, ops=[{"op": "assign", "uid": crew, "taskUid": u} for u in ids])
        after = {t["uid"]: t for t in mcp.call("tasks_query", handle=h, limit=100)["items"]}
        calendar = mcp.call("resources_write", handle=h, ops=[{"op": "update", "uid": crew, "calendar": "Standard"}])
    finally:
        mcp.close()
    moved = [u for u in before if (before[u]["start"], before[u]["finish"], before[u].get("duration"))
             != (after[u]["start"], after[u]["finish"], after[u].get("duration"))]
    check("assigning the crew moves no task and changes no duration", assigned["applied"] == len(ids) and not moved,
          f'{assigned["impact"]} moved {moved}')
    check("no task works outside the calendar's 08:00-17:00",
          all("08:00" <= t[f][11:16] <= "17:00" for t in after.values() for f in ("start", "finish") if t[f]),
          str([(t["start"], t["finish"]) for t in after.values()]))
    check("'update calendar' on the resource survives Project's calculation", calendar["applied"] == 1,
          json.dumps(calendar["rejected"]))

    # 2e. reschedule_incomplete against Project's own "reschedule uncompleted work to start after"
    # (UpdateProject, action 2). Found in the dry run: the task in progress was never moved.
    print("\nreschedule_incomplete against Project's own command")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(work / "late.xml"), create=True, name="Late", startDate="2026-11-02")["handle"]
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "T1", "duration": "10d"},
                                               {"op": "create", "name": "T2", "duration": "5d"},
                                               {"op": "create", "name": "T3", "duration": "3d"}])
        late = {t["name"]: t["uid"] for t in mcp.call("tasks_query", handle=h, limit=100)["items"] if not t["summary"]}
        mcp.call("links_write", handle=h, ops=[{"op": "link", "from": late["T1"], "to": late["T2"]}])
        mcp.call("schedule_update", handle=h, op="save_baseline", reason="test")
        mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": late["T1"], "actualStart": "2026-11-02T08:00:00",
                                                "percentComplete": 20}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2026-11-13T17:00:00")
        mcp.call("project_export", handle=h, path=str(work / "late-src.xml"), format="mspdi")
    finally:
        mcp.close()
    # Project splits in-progress tasks only where the file allows it, and takes the option only from the file.
    src = (work / "late-src.xml").read_text(encoding="utf-8")
    (work / "late-split.xml").write_text(re.sub(r"<SplitsInProgressTasks>0</SplitsInProgressTasks>",
                                                "<SplitsInProgressTasks>1</SplitsInProgressTasks>", src), encoding="utf-8")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(work / "late-split.xml"))["handle"]
        mcp.call("project_export", handle=h, path=str(work / "late.mpp"), format="mpp")
        h = mcp.call("project_open", path=str(work / "late.mpp"))["handle"]
        moved = mcp.call("schedule_update", handle=h, op="reschedule_incomplete")
        mcp.call("project_export", handle=h, path=str(work / "late-mcp.xml"), format="mspdi")
    finally:
        mcp.close()
    project_native(work / "late.mpp", work / "late-oracle.xml",
                   lambda pj: pj.Application.UpdateProject(True, pj.StatusDate, 2))
    same, total, bad = agree(tasks(work / "late-mcp.xml"), tasks(work / "late-oracle.xml"))
    check("reschedule_incomplete: applied", moved["applied"] == 1, json.dumps(moved["rejected"])[:300])
    check("reschedule_incomplete: dates match Project's own command on every task", same == total and total > 0,
          f"{same}/{total}, differ: {bad}")

    # 3. A guest in the user's Project.
    print("\nsharing a Project the user has open")
    import win32com.client
    exe = subprocess.run(["reg", "query", r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WINPROJ.EXE",
                          "/ve"], capture_output=True, text=True).stdout.split("REG_SZ")[-1].strip()
    subprocess.Popen([exe, "/s"])
    user = None
    for _ in range(90):
        time.sleep(1)
        try:
            user = win32com.client.GetActiveObject("MSProject.Application")
            break
        except Exception:
            pass
    if user is None:
        check("Project started for the guest test", False)
    else:
        user.FileNew()
        user.ActiveProject.Tasks.Add("Unsaved work of the user")
        theirs, pids = user.ActiveProject.Name, winproj_pids()
        mcp = Mcp()
        error = None
        try:
            h = mcp.call("project_open", path=str(work / "authored.mpp"))["handle"]
            mcp.call("schedule_update", handle=h, op="recalculate")
            mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": uid["Muros"], "duration": "14d"}],
                     dryRun=True)
            mcp.call("project_save", handle=h, op="save_as", path=str(work / "guest.mpp"), format="mpp")
            mcp.call("project_health", deep=True)
        except Exception as ex:
            error = str(ex)
        finally:
            mcp.close()
        names = [user.Projects(i).Name for i in range(1, user.Projects.Count + 1)]
        check("recalculate, dry run, .mpp save and deep health all work beside the user", error is None,
              error or "")
        check("the user's Project is still running", winproj_pids() == pids, f"{pids} -> {winproj_pids()}")
        check("the user's window is still visible", bool(user.Visible))
        check("only the user's document is open — nothing of ours left behind", names == [theirs], str(names))
        check("the user's unsaved work is intact and still active",
              user.ActiveProject.Name == theirs
              and user.ActiveProject.Tasks(1).Name == "Unsaved work of the user")
        user.FileCloseEx(0)
        user.Quit(0)

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
