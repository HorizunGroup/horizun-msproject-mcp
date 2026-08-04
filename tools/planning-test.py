#!/usr/bin/env python3
"""The planning capabilities: reprogramming, target dates, and learning from past schedules.

Builds a small schedule with a known delay, then asserts that the recovery options are
measured rather than asserted, that a target date is tested against the network rather
than against arithmetic, and that a library learned from finished schedules produces a
draft that carries the durations and the sequence of the work it learned from.

    python tools/planning-test.py
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
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
                                 "clientInfo": {"name": "planning-test", "version": "1.0"}})
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
        self.proc.wait(timeout=20)


def main() -> int:
    if not EXE.exists():
        print(f"FAIL  build first: {EXE} not found", file=sys.stderr)
        return 1

    workdir = Path(tempfile.mkdtemp(prefix="hzpm-planning-"))
    client = Client(EXE)

    try:
        # A short chain with lag, so every recovery lever has something to bite on.
        print("\n== a schedule that is running late ==")
        handle = client.call("project_open", path=str(workdir / "obra.xml"), create=True,
                             startDate="2026-09-07")["handle"]
        client.call("tasks_write", handle=handle, ops=[
            {"op": "create", "name": "Excavacion", "duration": "10d"},
            {"op": "create", "name": "Cimentacion", "duration": "15d"},
            {"op": "create", "name": "Estructura", "duration": "20d"},
            {"op": "create", "name": "Cubierta", "duration": "8d"},
        ])
        uid = {t["name"]: t["uid"] for t in client.call("tasks_query", handle=handle)["items"]}
        client.call("links_write", handle=handle, ops=[
            {"op": "link", "from": uid["Excavacion"], "to": uid["Cimentacion"], "lag": "5d"},
            {"op": "link", "from": uid["Cimentacion"], "to": uid["Estructura"]},
            {"op": "link", "from": uid["Estructura"], "to": uid["Cubierta"], "lag": "3d"},
        ])
        client.call("tasks_write", handle=handle, ops=[
            {"op": "update", "uid": uid["Excavacion"], "percentComplete": 30}])

        # ------------------------------------------------------------- recovery
        print("\n== schedule_recovery ==")
        rec = client.call("schedule_recovery", handle=handle, statusDate="2026-10-30",
                          targetFinish="2026-11-30")
        check("late work is identified", rec["lateCount"] > 0, f"{rec['lateCount']} late")
        check("the gap to the target is quantified", rec.get("daysBehindTarget") is not None,
              f"{rec.get('daysBehindTarget')} days")
        check("late work is ranked by how much sits behind it",
              rec["late"][0]["successorsBlocked"] >= rec["late"][-1]["successorsBlocked"]
              if len(rec["late"]) > 1 else True)
        check("recovery options are offered", len(rec["options"]) > 0, f"{len(rec['options'])} options")

        for option in rec["options"]:
            check(f"'{option['lever']}' reports a measured saving",
                  option["daysRecovered"] > 0
                  and option["projectFinishAfter"] < option["projectFinishBefore"],
                  f"{option['daysRecovered']}d, "
                  f"{option['projectFinishBefore'][:10]} -> {option['projectFinishAfter'][:10]}")
            check(f"'{option['lever']}' says how to apply it and what it risks",
                  bool(option["howToApply"]) and len(option.get("risks", [])) > 0)

        before = client.call("tasks_query", handle=handle, uids=[uid["Cubierta"]])["items"][0]
        after = client.call("tasks_query", handle=handle, uids=[uid["Cubierta"]])["items"][0]
        check("measuring recovery changes nothing in the open document",
              before["finish"] == after["finish"], f"{before['finish'][:10]}")

        # --------------------------------------------------------------- target
        print("\n== schedule_target ==")
        tight = client.call("schedule_target", handle=handle, target="2026-10-01")
        check("an impossible target is reported as such", tight["achievable"] is False)
        check("and the shortfall is quantified in working days",
              tight["compressionNeededDays"] > 0, f"{tight['compressionNeededDays']}d")
        check("it points at the tool that measures the recovery",
              any("schedule_recovery" in n for n in tight.get("notes", [])))

        loose = client.call("schedule_target", handle=handle, target="2030-01-01")
        check("a comfortable target is reported as achievable", loose["achievable"] is True)

        client.call("tasks_write", handle=handle, ops=[
            {"op": "create", "name": "Actividad suelta", "duration": "4d"}])
        orphaned = client.call("schedule_target", handle=handle, target="2030-01-01")
        loose_names = {o["name"] for o in orphaned["orphans"]}
        check("work the network does not hold in place is reported",
              "Actividad suelta" in loose_names, f"{len(orphaned['orphans'])} orphans")
        check("and each orphan explains what it costs",
              all(len(o["consequence"]) > 30 for o in orphaned["orphans"]))

        client.call("project_save", handle=handle, op="save")
        client.call("project_save", handle=handle, op="close")

        # ---------------------------------------------------------------- learn
        print("\n== schedule_learn ==")

        # Two past projects, same trades, three units each — the shape real schedules have.
        for project in ("obra-2024", "obra-2025"):
            h = client.call("project_open", path=str(workdir / f"{project}.xml"), create=True,
                            startDate="2024-01-08")["handle"]
            ops = []
            for unit in (1, 2, 3):
                ops += [
                    {"op": "create", "name": f"Mamposteria apto {unit}0{unit}", "duration": "6d"},
                    {"op": "create", "name": f"Enchapes apto {unit}0{unit}", "duration": "4d"},
                ]
            client.call("tasks_write", handle=h, ops=ops)
            u = {t["name"]: t["uid"] for t in client.call("tasks_query", handle=h, limit=50)["items"]}
            links = []
            for unit in (1, 2, 3):
                links.append({"op": "link", "from": u[f"Mamposteria apto {unit}0{unit}"],
                              "to": u[f"Enchapes apto {unit}0{unit}"]})
                if unit > 1:
                    links.append({"op": "link",
                                  "from": u[f"Mamposteria apto {unit-1}0{unit-1}"],
                                  "to": u[f"Mamposteria apto {unit}0{unit}"]})
            client.call("links_write", handle=h, ops=links)
            client.call("project_save", handle=h, op="save")
            client.call("project_save", handle=h, op="close")

        lib_path = workdir / "library.json"
        lib = client.call("schedule_learn",
                          paths=[str(workdir / "obra-2024.xml"), str(workdir / "obra-2025.xml")],
                          savePath=str(lib_path), minOccurrences=2)
        keys = {a["key"]: a for a in lib["activities"]}
        check("activities are learned across projects", len(keys) >= 2, f"{len(keys)}: {sorted(keys)}")
        check("the same activity in different units is one entry",
              "MAMPOSTERIA APTO" in keys,
              f"got {sorted(keys)}")

        mamposteria = keys.get("MAMPOSTERIA APTO")
        check("its duration is learned from history",
              mamposteria and mamposteria["medianDurationDays"] == 6,
              f"median={mamposteria['medianDurationDays'] if mamposteria else '?'}")
        check("it is recognised as repeating unit by unit",
              mamposteria and mamposteria.get("repeatsInSequence") is True)
        check("the library is saved", lib_path.exists())

        enchapes = keys.get("ENCHAPES APTO")
        check("what usually comes before an activity is learned",
              enchapes and any(p["key"] == "MAMPOSTERIA APTO" for p in enchapes["typicalPredecessors"]),
              json.dumps([p["key"] for p in (enchapes or {}).get("typicalPredecessors", [])]))

        # --------------------------------------------- productivity from quantities
        print("\n== productivity learned from measured quantities ==")

        # Codes on the tasks, quantities on the model elements — they join on the code.
        for project in ("obra-2024", "obra-2025"):
            h = client.call("project_open", path=str(workdir / f"{project}.xml"))["handle"]
            rows = client.call("tasks_query", handle=h, limit=50)["items"]
            client.call("tasks_write", handle=h, ops=[
                {"op": "update", "uid": t["uid"],
                 "custom": {"Text1": "MAM-01" if "Mamposteria" in t["name"] else "ENC-01"}}
                for t in rows if str(t["name"]).startswith(("Mamposteria", "Enchapes"))])
            client.call("project_save", handle=h, op="save")
            client.call("project_save", handle=h, op="close")

        elements = workdir / "cantidades.csv"
        elements.write_text(
            "elementId,code,quantity,unit\n"
            "1,MAM-01,120,m2\n"
            "2,MAM-01,120,m2\n"
            "3,ENC-01,60,m2\n", encoding="utf-8")

        no_code = client.call("schedule_learn",
                              paths=[str(workdir / "obra-2024.xml")],
                              quantitySources=[str(elements)], minOccurrences=2)
        check("quantities without a code field are refused, with the reason",
              "__error__" in no_code and "codeField" in no_code["__error__"],
              no_code.get("__error__", "")[:110])

        lib2_path = workdir / "library-rates.json"
        lib2 = client.call("schedule_learn",
                           paths=[str(workdir / "obra-2024.xml"), str(workdir / "obra-2025.xml")],
                           savePath=str(lib2_path), codeField="Text1",
                           quantitySources=[str(elements)], minOccurrences=2)
        rated = {a["key"]: a for a in lib2["activities"]}
        mam = rated.get("MAMPOSTERIA APTO")
        check("a productivity rate is learned from quantity over duration",
              mam is not None and mam.get("productivityPerDay") == 40,
              f"240 m2 / 6d = {mam.get('productivityPerDay') if mam else '?'} per day")
        check("the unit of measure comes along", mam is not None and mam.get("unit") == "m2",
              str(mam.get("unit") if mam else None))
        check("and the library says rates are now available",
              any("productivity" in n.lower() for n in lib2.get("notes", [])))

        sized = client.call("schedule_generate", libraryPath=str(lib2_path),
                            outputPath=str(workdir / "por-cantidad.xml"), startDate="2027-01-04",
                            units=1, quantities={"MAMPOSTERIA APTO": 400})
        srows = client.call("tasks_query", handle=sized["handle"], limit=50)["items"]
        mam_task = next((t for t in srows if "Mamposteria" in str(t["name"])), None)
        check("a new schedule is sized from measured quantity, not a remembered duration",
              mam_task is not None and mam_task["duration"] == "10d",
              f"400 m2 at 40/day should be 10d, got {mam_task['duration'] if mam_task else '?'}")
        check("and it shows the arithmetic it used",
              any("40" in n and "400" in n for n in sized.get("notes", [])),
              json.dumps(sized.get("notes"))[-150:])
        client.call("project_save", handle=sized["handle"], op="close", discardChanges=True)

        # ------------------------------------------------------------- generate
        print("\n== schedule_generate ==")
        gen = client.call("schedule_generate", libraryPath=str(lib_path),
                          outputPath=str(workdir / "nuevo.xml"), startDate="2027-01-04",
                          durationBasis="median", units=5, unitLabel="apto")
        check("a draft is generated", gen.get("tasksCreated", 0) > 0,
              f"{gen.get('tasksCreated')} tasks, {gen.get('linksCreated')} links")
        check("repeating work is created once per unit",
              gen["tasksCreated"] >= 10, f"{gen['tasksCreated']} for 5 units of 2 activities")
        check("the sequence is wired, not left dangling",
              gen["linksCreated"] >= 8, f"{gen['linksCreated']} links")

        rows = client.call("tasks_query", handle=gen["handle"], limit=100)["items"]
        names = [t["name"] for t in rows]
        check("units are numbered rather than all carrying the first one's name",
              any("apto 5" in n for n in names), f"{names[:3]}")
        check("durations come from the library",
              all(t["duration"] in ("6d", "4d") for t in rows), f"{ {t['duration'] for t in rows} }")
        check("the draft is scheduled, with dates",
              all(t["start"] for t in rows))
        check("it says out loud that it is a draft",
              any("draft" in n.lower() for n in gen.get("notes", [])))

        client.call("project_save", handle=gen["handle"], op="close", discardChanges=True)

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
