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
