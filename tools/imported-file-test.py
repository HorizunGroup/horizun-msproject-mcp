#!/usr/bin/env python3
"""Building on a schedule Microsoft Project saved, rather than one this server began.

MPXJ numbers what is added to a schedule it built itself, but not to one read from a file, and a
file Project saved carries things one begun here does not: a project summary row, an unassigned
resource, availability tables. Each defect below was found building a schedule on such a file:

  * a new resource came out with no unique id, was rejected, stayed in the model and broke every
    later save
  * new tasks had no outline level and a WBS of "0", and landed a level too high in the .mpp
  * tasks_write custom wrote Number1 and WBS into Text1
  * a crew of eight read as MaxUnits 100 (availability starting in the future), and two tasks one
    after the other on the same day were counted as working at once

The fixture is the MSPDI of a Project-saved base (calendar 'Obra L-S', a resource named 'Peón').
Runs on the internal engine and never starts Microsoft Project, so it is safe on any machine.

    python tools/imported-file-test.py
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

WORKDAY = ("<WorkingTimes><WorkingTime><FromTime>08:00:00</FromTime><ToTime>12:00:00</ToTime></WorkingTime>"
           "<WorkingTime><FromTime>13:00:00</FromTime><ToTime>17:00:00</ToTime></WorkingTime></WorkingTimes>")
ESTANDAR = ("<WorkingTimes><WorkingTime><FromTime>09:00:00</FromTime><ToTime>13:00:00</ToTime></WorkingTime>"
            "<WorkingTime><FromTime>15:00:00</FromTime><ToTime>19:00:00</ToTime></WorkingTime></WorkingTimes>")


def weekdays(hours: str, saturday: bool) -> str:
    days = "<WeekDay><DayType>1</DayType><DayWorking>0</DayWorking></WeekDay>"
    for day in range(2, 7):
        days += f"<WeekDay><DayType>{day}</DayType><DayWorking>1</DayWorking>{hours}</WeekDay>"
    days += (f"<WeekDay><DayType>7</DayType><DayWorking>1</DayWorking>{hours}</WeekDay>" if saturday
             else "<WeekDay><DayType>7</DayType><DayWorking>0</DayWorking></WeekDay>")
    return f"<WeekDays>{days}</WeekDays>"


def resource(uid: int, name: str, units: float, calendar: int) -> str:
    # Available from the project start, next month: MPXJ answers MaxUnits from the row covering today.
    return (f"<Resource><UID>{uid}</UID><ID>{uid}</ID><Name>{name}</Name><Type>1</Type><IsNull>0</IsNull>"
            f"<MaxUnits>{units / 100:.2f}</MaxUnits><CalendarUID>{calendar}</CalendarUID><AvailabilityPeriods>"
            "<AvailabilityPeriod><AvailableFrom>2036-10-06T08:00:00</AvailableFrom>"
            f"<AvailableTo>2049-12-31T23:59:00</AvailableTo><AvailableUnits>{units / 100:g}</AvailableUnits>"
            "</AvailabilityPeriod></AvailabilityPeriods></Resource>")


CREW = [(1, "Operario", 600), (2, "Oficial", 400), (3, "Peón", 800), (4, "Electricista", 200), (5, "Gasfitero", 200)]

FIXTURE = f"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Project xmlns="http://schemas.microsoft.com/project">
<SaveVersion>14</SaveVersion><Name>base.xml</Name><Title>base</Title><ScheduleFromStart>1</ScheduleFromStart>
<StartDate>2036-10-06T08:00:00</StartDate><CalendarUID>3</CalendarUID>
<DefaultStartTime>08:00:00</DefaultStartTime><DefaultFinishTime>17:00:00</DefaultFinishTime>
<MinutesPerDay>480</MinutesPerDay><MinutesPerWeek>2880</MinutesPerWeek><DaysPerMonth>24</DaysPerMonth>
<Calendars>
<Calendar><UID>1</UID><Name>Estándar</Name><IsBaseCalendar>1</IsBaseCalendar><BaseCalendarUID>-1</BaseCalendarUID>{weekdays(ESTANDAR, False)}</Calendar>
<Calendar><UID>3</UID><Name>Obra L-S</Name><IsBaseCalendar>1</IsBaseCalendar><BaseCalendarUID>-1</BaseCalendarUID>{weekdays(WORKDAY, True)}</Calendar>
<Calendar><UID>9</UID><Name>Unnamed Resource</Name><IsBaseCalendar>0</IsBaseCalendar><BaseCalendarUID>1</BaseCalendarUID></Calendar>
{"".join(f"<Calendar><UID>{10 + uid}</UID><Name>{name}</Name><IsBaseCalendar>0</IsBaseCalendar><BaseCalendarUID>3</BaseCalendarUID></Calendar>" for uid, name, _ in CREW)}
</Calendars>
<Tasks><Task><UID>0</UID><ID>0</ID><Name>base</Name><Type>1</Type><IsNull>0</IsNull><WBS>0</WBS>
<OutlineNumber>0</OutlineNumber><OutlineLevel>0</OutlineLevel><Start>2036-10-06T08:00:00</Start>
<Finish>2036-10-06T08:00:00</Finish><Duration>PT0H0M0S</Duration><Summary>1</Summary></Task></Tasks>
<Resources>
<Resource><UID>0</UID><ID>0</ID><Type>1</Type><IsNull>0</IsNull><MaxUnits>1.00</MaxUnits><CalendarUID>9</CalendarUID></Resource>
{"".join(resource(uid, name, units, 10 + uid) for uid, name, units in CREW)}
</Resources>
</Project>
"""


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
                                "clientInfo": {"name": "imported-file-test", "version": "1"}})
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


def tasks(mcp: Mcp, handle: str) -> dict[str, dict]:
    return {t["name"]: t for t in mcp.call("tasks_query", handle=handle, limit=1000, sort="wbs")["items"]}


def xml_tasks(path: Path) -> dict[str, dict[str, str]]:
    text = path.read_text(encoding="utf-8")
    out = {}
    for block in re.findall(r"<Task>(.*?)</Task>", text, re.S):
        fields = dict(re.findall(r"<(UID|Name|WBS|OutlineLevel)>([^<]*)<", block))
        attributes = re.findall(r"<ExtendedAttribute>\s*<FieldID>(\d+)</FieldID>\s*<Value>([^<]*)<", block)
        fields["ext"] = dict(attributes)
        out[fields.get("Name", "")] = fields
    return out


def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-imported-"))
    base = work / "base.xml"
    base.write_text(FIXTURE, encoding="utf-8")
    mcp = Mcp()
    try:
        h = mcp.call("project_open", path=str(base))["handle"]

        print("resources on a file Project saved")
        crew = {r.get("name"): r for r in mcp.call("resources_query", handle=h)["items"]}
        check("max units are the availability Project shows, not MPXJ's 100 for 'none today'",
              [crew[name].get("maxUnits") for _, name, _ in CREW] == [u for _, _, u in CREW],
              str({n: r.get("maxUnits") for n, r in crew.items()}))

        r = mcp.call("resources_write", handle=h, ops=[{"op": "create", "name": "Capataz", "maxUnits": 100}])
        created = [x for x in mcp.call("resources_query", handle=h)["items"] if x.get("name") == "Capataz"]
        check("a resource created on an imported file is applied", r["applied"] == 1 and not r["rejected"],
              json.dumps(r["rejected"])[:300])
        check("and gets a unique id of its own",
              len(created) == 1 and created[0]["uid"] > max(uid for uid, _, _ in CREW), json.dumps(created))

        print("\ntasks under summaries")
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Preliminares"}])
        top = tasks(mcp, h)["Preliminares"]["uid"]
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Obras provisionales", "parentUid": top}])
        mid = tasks(mcp, h)["Obras provisionales"]["uid"]
        mcp.call("tasks_write", handle=h, ops=[
            {"op": "create", "name": "Movilización", "parentUid": mid, "duration": "4h"},
            {"op": "create", "name": "Cerco", "parentUid": mid, "duration": "4h"},
            {"op": "create", "name": "Inicio de obra", "parentUid": top, "duration": "0d"},
        ])
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Movimiento de tierras"}])
        rows = tasks(mcp, h)
        levels = {n: rows[n].get("outlineLevel") for n in
                  ["Preliminares", "Obras provisionales", "Movilización", "Cerco", "Inicio de obra", "Movimiento de tierras"]}
        check("new tasks take the outline level of where they were put",
              levels == {"Preliminares": 1, "Obras provisionales": 2, "Movilización": 3, "Cerco": 3,
                         "Inicio de obra": 2, "Movimiento de tierras": 1}, str(levels))
        wbs = {n: rows[n].get("wbs") for n in levels}
        check("and a WBS that follows the outline, not '0'",
              wbs == {"Preliminares": "1", "Obras provisionales": "1.1", "Movilización": "1.1.1", "Cerco": "1.1.2",
                      "Inicio de obra": "1.2", "Movimiento de tierras": "2"}, str(wbs))

        print("\ncustom fields")
        leaf = rows["Cerco"]["uid"]
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": leaf, "custom": {
            "Text2": "CWA-3", "Number1": "12.5", "Flag1": "true", "Date1": "2036-11-02", "WBS": "1.1.X"}}])
        check("Text, Number, Flag, Date and WBS are all applied", r["applied"] == 1 and not r["rejected"],
              json.dumps(r["rejected"])[:300])
        r = mcp.call("tasks_write", handle=h, ops=[{"op": "update", "uid": leaf, "custom": {"Cost1": "5", "Foo": "x"}}])
        check("a key that is not a supported field is rejected, by name",
              r["applied"] == 0 and {x["field"] for x in r["rejected"]} == {"Cost1", "Foo"}, json.dumps(r)[:300])
        check("the WBS given is the task's WBS", tasks(mcp, h)["Cerco"].get("wbs") == "1.1.X")

        print("\nassignments and overallocation")
        a, b = rows["Movilización"]["uid"], rows["Cerco"]["uid"]
        mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b}])
        # Monday 08:00-12:00, then 13:00-17:00 the same day.
        mcp.call("schedule_update", handle=h, op="recalculate")
        r = mcp.call("resources_write", handle=h, ops=[
            {"op": "assign", "uid": 3, "taskUid": a, "units": 800},
            {"op": "assign", "uid": 3, "taskUid": b, "units": 800},
            {"op": "assign", "uid": 4, "taskUid": b, "units": 200},
        ])
        check("the assignments are applied", r["applied"] == 3, json.dumps(r["rejected"])[:300])
        peon = next(x for x in mcp.call("resources_query", handle=h, name="Pe")["items"] if x["uid"] == 3)
        check("two tasks one after the other on the same day are not concurrent",
              peon.get("overallocated") is False, json.dumps(peon))
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Trazo", "parentUid": top, "duration": "4h"}])
        mcp.call("schedule_update", handle=h, op="recalculate")
        extra = tasks(mcp, h)["Trazo"]["uid"]
        mcp.call("resources_write", handle=h, ops=[{"op": "assign", "uid": 3, "taskUid": extra, "units": 100}])
        peon = next(x for x in mcp.call("resources_query", handle=h, name="Pe")["items"] if x["uid"] == 3)
        check("but two at the same hour above the crew's eight are", peon.get("overallocated") is True,
              json.dumps(peon))

        print("\nsaving")
        out = work / "saved.xml"
        saved = mcp.call("project_save", handle=h, op="save_as", path=str(out), format="mspdi")
        notes = " ".join(saved.get("notes") or [])
        check("the schedule saves, and reads back whole", "all preserved" in notes, notes[:300])
        written = xml_tasks(out)
        check("the file carries the outline levels",
              [written[n].get("OutlineLevel") for n in ["Preliminares", "Obras provisionales", "Movilización"]]
              == ["1", "2", "3"], str({n: written[n].get("OutlineLevel") for n in levels}))
        # Project's field ids: Number1 188743767, Text1 188743731, Text2 188743734.
        ext = written["Cerco"]["ext"]
        check("Number1 is written to Number1, and nothing to Text1",
              ext.get("188743767", "").startswith("12.5") and "188743731" not in ext
              and ext.get("188743734") == "CWA-3", str(ext))
        text = out.read_text(encoding="utf-8")
        resources = dict(re.findall(r"<Resource>\s*<UID>(\d+)</UID>.*?<Name>([^<]*)</Name>", text, re.S))
        assigned = re.findall(r"<Assignment>(.*?)</Assignment>", text, re.S)
        to_b = sorted(resources.get(re.search(r"<ResourceUID>(\d+)<", x).group(1), "?")
                      for x in assigned if re.search(rf"<TaskUID>{b}</TaskUID>", x))
        check("the assignments name the resources they were made to, 'Peón' included",
              to_b == ["Electricista", "Peón"], str(to_b))
        check("every assignment has a unique id", all("<UID>" in x for x in assigned))
    finally:
        mcp.close()

    print(f"\n{passed} passed, {len(failures)} failed")
    if failures:
        sys.exit(1)


if __name__ == "__main__":
    main()
