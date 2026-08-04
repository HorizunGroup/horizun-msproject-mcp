#!/usr/bin/env python3
"""End-to-end acceptance test for horizun-msproject-mcp.

Builds a small construction schedule from nothing, then drives every tool over real
JSON-RPC and asserts the contracts the design rests on:

  * identity is the stable uid, never the row id
  * a write is only counted as applied after it was re-read from the model
  * a rejected write explains itself and says what the value actually became
  * a dry run measures real impact and commits nothing
  * capabilities the backend cannot honour are refused, not approximated

    python tools/acceptance-test.py
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0" / "horizun-msproject-mcp.exe"

CHECKS = 0
FAILURES: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> bool:
    global CHECKS
    CHECKS += 1
    if not ok:
        FAILURES.append(label)
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}{f' -- {detail}' if detail else ''}")
    return ok


class Client:
    def __init__(self, exe: Path) -> None:
        self.proc = subprocess.Popen(
            [str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
        )
        self._id = 0
        self._rpc("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "acceptance", "version": "1.0"},
        })
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

    def tools(self) -> dict:
        return {t["name"]: t for t in self._rpc("tools/list", {})["tools"]}

    def call(self, tool_name: str, /, **args):
        """Returns the parsed tool payload, or an {'__error__': msg} dict for a tool-level failure.

        Positional-only so a tool argument called 'name' cannot collide with it.
        """
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


def main() -> int:
    if not EXE.exists():
        print(f"FAIL  build first: {EXE} not found", file=sys.stderr)
        return 1

    workdir = Path(tempfile.mkdtemp(prefix="hzpm-acceptance-"))
    project_path = workdir / "torre-a.xml"
    client = Client(EXE)

    try:
        # ---------------------------------------------------------------- tools
        print("\n== tool surface ==")
        tools = client.tools()
        expected = {
            "project_health", "project_open", "project_save",
            "project_info", "tasks_query", "links_query", "resources_query", "timephased_query",
            "schedule_analyze", "schedule_qa", "baseline_compare",
            "tasks_write", "links_write", "resources_write", "calendars_write", "schedule_update",
            "project_export", "project_import", "bim_link", "bim_sync",
        }
        missing = expected - set(tools)
        check(f"all {len(expected)} designed tools are advertised", not missing, f"missing: {sorted(missing)}")
        print(f"  advertised: {len(tools)} tools")

        health = client.call("project_health")
        backend = health.get("backend")
        print(f"  backend: {backend}")

        # ------------------------------------------------------- build from zero
        print("\n== build a schedule from nothing ==")
        opened = client.call("project_open", path=str(project_path), create=True,
                             name="Torre A", startDate="2026-09-01")
        handle = opened["handle"]
        check("created an empty schedule", bool(handle))

        created = client.call("tasks_write", handle=handle, ops=[
            {"op": "create", "name": "Estructura N+3", "duration": "0"},
            {"op": "create", "name": "Encofrado losa", "duration": "5d", "custom": {"Text1": "D021-A1-A01"}},
            {"op": "create", "name": "Armado losa", "duration": "4d", "custom": {"Text1": "D021-A1-A02"}},
            {"op": "create", "name": "Vaciado losa", "duration": "2d", "custom": {"Text1": "D021-A1-A03"}},
            {"op": "create", "name": "Hito: losa terminada", "duration": "0", "milestone": True},
        ])
        check("created 5 tasks", created["applied"] == 5, f"applied={created['applied']} rejected={created['rejected']}")

        rows = client.call("tasks_query", handle=handle, limit=100)["items"]
        by_name = {t["name"]: t["uid"] for t in rows}
        check("every task has a stable uid", all(t["uid"] > 0 for t in rows))

        # --------------------------------------------------------------- linking
        print("\n== dependency network ==")
        linked = client.call("links_write", handle=handle, ops=[
            {"op": "link", "from": by_name["Encofrado losa"], "to": by_name["Armado losa"], "type": "FS"},
            {"op": "link", "from": by_name["Armado losa"], "to": by_name["Vaciado losa"], "type": "FS", "lag": "1d"},
            {"op": "link", "from": by_name["Vaciado losa"], "to": by_name["Hito: losa terminada"], "type": "FS"},
        ])
        check("created 3 dependencies", linked["applied"] == 3,
              f"applied={linked['applied']} rejected={linked['rejected']}")

        cycle = client.call("links_write", handle=handle, ops=[
            {"op": "link", "from": by_name["Hito: losa terminada"], "to": by_name["Encofrado losa"]},
        ])
        cycle_rejected = cycle["applied"] == 0 and any("cycle" in r["reason"].lower() for r in cycle["rejected"])
        check("a cycle is detected and refused before it is applied", cycle_rejected,
              json.dumps(cycle["rejected"]))

        # ------------------------------------------------------------- resources
        print("\n== resources ==")
        res = client.call("resources_write", handle=handle, ops=[
            {"op": "create", "name": "Cuadrilla 1", "type": "Work", "maxUnits": 100},
        ])
        check("created a resource", res["applied"] == 1, json.dumps(res["rejected"]))

        res_uid = client.call("resources_query", handle=handle)["items"][0]["uid"]
        assigned = client.call("resources_write", handle=handle, ops=[
            {"op": "assign", "uid": res_uid, "taskUid": by_name["Encofrado losa"], "units": 100},
            {"op": "assign", "uid": res_uid, "taskUid": by_name["Armado losa"], "units": 100},
        ])
        check("assigned the resource to 2 tasks", assigned["applied"] == 2, json.dumps(assigned["rejected"]))

        # ---------------------------------------------------------- save + reopen
        print("\n== persistence ==")
        saved = client.call("project_save", handle=handle, op="save", format="mspdi")
        check("saved to disk", Path(saved["path"]).exists(), saved.get("path", ""))
        client.call("project_save", handle=handle, op="close")

        reopened = client.call("project_open", path=str(project_path))
        handle = reopened["handle"]
        check("reopened with all tasks intact", reopened["tasks"] >= 5, f"tasks={reopened['tasks']}")
        check("reopened with the resource intact", reopened["resources"] == 1)

        rows = client.call("tasks_query", handle=handle, limit=100)["items"]
        by_name = {t["name"]: t["uid"] for t in rows}
        check("the budget code survived the round trip",
              any((t.get("custom") or {}).get("Text1") == "D021-A1-A01"
                  for t in client.call("tasks_query", handle=handle, includeCustom=True, limit=100)["items"]))

        # --------------------------------------------------------------- reading
        print("\n== reads ==")
        info = client.call("project_info", handle=handle)
        check("project_info reports counts", info["tasks"] >= 5 and info["leafTasks"] >= 1)

        page = client.call("tasks_query", handle=handle, limit=2)
        check("queries paginate", page["returned"] == 2 and page["nextCursor"] == 2,
              f"returned={page['returned']} cursor={page['nextCursor']}")

        milestones = client.call("tasks_query", handle=handle, milestone=True, limit=50)
        check("the milestone filter works", milestones["total"] >= 1)

        subgraph = client.call("links_query", handle=handle, uids=[by_name["Armado losa"]], depth=1)
        check("links_query returns the subgraph", subgraph["total"] >= 2, f"total={subgraph['total']}")

        # -------------------------------------------------------------- analysis
        print("\n== analysis ==")
        analysis = client.call("schedule_analyze", handle=handle,
                               aspects=["critical_path", "float_distribution", "dependency_health", "milestones"])
        check("critical path computed", analysis.get("criticalPath") is not None)
        check("dependency health computed",
              analysis["dependencyHealth"]["finishStart"] == 3,
              json.dumps(analysis.get("dependencyHealth")))
        check("milestones reported", len(analysis.get("milestones") or []) >= 1)

        qa = client.call("schedule_qa", handle=handle, statusDate="2026-09-15", budgetCodeField="Text1")
        rules = {f["rule"] for f in qa["findings"]}
        dcma = {r for r in rules if r.startswith("dcma_")}
        check("all 14 DCMA checks ran", len(dcma) == 14, f"got {len(dcma)}: {sorted(dcma)}")
        check("Horizun rules ran too", any(r.startswith("hrz_") for r in rules))

        cpt = next(f for f in qa["findings"] if f["rule"] == "dcma_12_critical_path_test")
        check("the Critical Path Test actually injects a delay and measures the result",
              cpt.get("evaluated") is True and cpt.get("measured") is not None,
              cpt["summary"])
        check("the Critical Path Test passes on a correctly linked chain",
              cpt.get("passed") is True, cpt["summary"])
        check("checks that cannot be evaluated say why", qa["notEvaluated"] >= 1,
              f"notEvaluated={qa['notEvaluated']}")

        # --------------------------------------------- the critical-path engine
        print("\n== critical-path engine ==")
        working = [t for t in client.call("tasks_query", handle=handle, limit=100, sort="start")["items"]
                   if not t["summary"]]
        check("every task got a computed start and finish",
              all(t["start"] and t["finish"] for t in working),
              f"{sum(1 for t in working if not t['start'])} without dates")

        chain = {t["name"]: t for t in working}
        enc, arm, vac = chain["Encofrado losa"], chain["Armado losa"], chain["Vaciado losa"]
        check("a finish-to-start successor starts after its predecessor finishes",
              arm["start"] > enc["finish"], f"{enc['finish']} -> {arm['start']}")
        check("lag is honoured between Armado and Vaciado",
              vac["start"] > arm["finish"], f"{arm['finish']} -> {vac['start']}")
        check("the driving chain is flagged critical",
              all(chain[n]["critical"] for n in ("Encofrado losa", "Armado losa", "Vaciado losa")),
              json.dumps({n: chain[n]["critical"] for n in chain}))
        check("float is computed, not left null",
              all(t["totalFloatDays"] is not None for t in working))
        check("no task is scheduled to start on a weekend",
              all(date.fromisoformat(t["start"][:10]).weekday() < 5 for t in working if t["start"]))

        # ------------------------------------------------------- baseline and EVM
        print("\n== baseline and earned value ==")
        base = client.call("schedule_update", handle=handle, op="save_baseline", baseline=0)
        check("baseline saved", base["applied"] == 1, json.dumps(base["rejected"]))

        client.call("tasks_write", handle=handle, ops=[
            {"op": "update", "uid": by_name["Encofrado losa"], "percentComplete": 100,
             "actualStart": "2026-09-01", "actualFinish": "2026-09-05"},
            {"op": "update", "uid": by_name["Armado losa"], "percentComplete": 50},
        ])

        evm = client.call("baseline_compare", handle=handle, statusDate="2026-09-15")
        check("EVM reports the baseline is present", evm["baselinePresent"] is True)
        check("EVM produces the standard metrics",
              all(k in evm["project"] for k in ("bcws", "bcwp", "acwp", "bac")))

        curve = client.call("timephased_query", handle=handle, measure="work", granularity="week")
        check("the S-curve is cumulative and monotonic",
              all(b["value"] >= a["value"] for a, b in
                  zip(curve["cumulative"], curve["cumulative"][1:])) if curve["cumulative"] else True)

        # -------------------------------------------- verified writes and dry run
        print("\n== the write contract ==")
        before = client.call("tasks_query", handle=handle, uids=[by_name["Vaciado losa"]])["items"][0]

        dry = client.call("tasks_write", handle=handle, dryRun=True, ops=[
            {"op": "update", "uid": by_name["Vaciado losa"], "duration": "30d"},
        ])
        after_dry = client.call("tasks_query", handle=handle, uids=[by_name["Vaciado losa"]])["items"][0]
        check("a dry run reports impact", dry["impact"] is not None)
        check("a dry run commits nothing",
              after_dry["duration"] == before["duration"],
              f"{before['duration']} -> {after_dry['duration']}")
        check("a dry run says so", dry["dryRun"] is True)

        real = client.call("tasks_write", handle=handle, ops=[
            {"op": "update", "uid": by_name["Vaciado losa"], "duration": "30d"},
        ])
        after_real = client.call("tasks_query", handle=handle, uids=[by_name["Vaciado losa"]])["items"][0]
        check("a real write applies and is verified by re-reading",
              real["applied"] == 1 and after_real["duration"] != before["duration"],
              f"applied={real['applied']} duration={after_real['duration']}")
        check("the write result names its verification method", real["verifiedBy"] == "reread")

        bad = client.call("tasks_write", handle=handle, ops=[
            {"op": "update", "uid": 999999, "name": "does not exist"},
        ])
        check("a write to an unknown uid is rejected, not silently ignored",
              bad["applied"] == 0 and len(bad["rejected"]) == 1)
        check("the rejection explains itself",
              len(bad["rejected"][0]["reason"]) > 20, bad["rejected"][0]["reason"])

        # ------------------------------------------- capability honesty (contract)
        print("\n== capability honesty ==")
        engine = client.call("schedule_update", handle=handle, op="level_resources")
        refused = "__error__" in engine and "not available on this backend" in engine["__error__"]
        check("resource levelling is refused rather than imitated",
              refused if backend == "mpxj" else True,
              engine.get("__error__", "")[:140])

        recalc = client.call("schedule_update", handle=handle, op="recalculate")
        check("recalculation is available on the file backend and succeeds",
              recalc["applied"] == 1, json.dumps(recalc["rejected"]))

        mpp_path = workdir / "out.mpp"
        mpp = client.call("project_save", handle=handle, op="save_as",
                          path=str(mpp_path), format="mpp")
        if "__error__" in mpp:
            check("native .mpp is refused with a usable reason when Project is unavailable",
                  "mspdi" in mpp["__error__"].lower(), mpp["__error__"][:140])
        else:
            check("native .mpp is written through Microsoft Project",
                  mpp_path.exists() and mpp_path.stat().st_size > 0,
                  f"{mpp_path.stat().st_size if mpp_path.exists() else 0} bytes")

        # ---------------------------------------------------------------- export
        print("\n== export and import ==")
        csv_path = workdir / "tasks.csv"
        exported = client.call("project_export", handle=handle, path=str(csv_path), format="csv")
        check("CSV export written", Path(exported["path"]).exists() and exported["rows"] >= 1)

        pbip = client.call("project_export", handle=handle,
                           path=str(workdir / "dataset.json"), format="pbip_dataset")
        dataset = json.loads(Path(pbip["path"]).read_text(encoding="utf-8"))
        check("the Power BI dataset carries tasks, EVM and quality",
              all(k in dataset for k in ("tasks", "earnedValue", "quality")))

        import_path = workdir / "progress.csv"
        import_path.write_text(
            "uid,percentComplete\n"
            f"{by_name['Vaciado losa']},75\n", encoding="utf-8")

        plan = client.call("project_import", handle=handle, path=str(import_path))
        check("import plans before it writes", plan["applied"] is False and len(plan["changes"]) == 1,
              json.dumps(plan["changes"]))

        applied = client.call("project_import", handle=handle, path=str(import_path), apply=True)
        check("import applies through the verified write path",
              applied["applied"] is True and applied["writeResult"]["applied"] == 1,
              json.dumps(applied.get("writeResult", {}).get("rejected")))

        # ------------------------------------------------------------------- BIM
        print("\n== BIM bridge ==")
        elements_path = workdir / "elements.csv"
        elements_path.write_text(
            "elementId,code,category,quantity,unit\n"
            "354001,D021-A1-A01,Floors,120,m2\n"
            "354002,D021-A1-A01,Floors,80,m2\n"
            "354003,D021-A1-A02,StructuralRebar,1500,kg\n"
            "354004,D021-A1-A03,Floors,45,m3\n"
            "354005,D099-Z9-Z99,Walls,10,m2\n", encoding="utf-8")

        suggestion = client.call("bim_link", handle=handle, op="suggest",
                                 elementsPath=str(elements_path), codeField="Text1")
        check("BIM matching ties tasks to elements by code", suggestion["mapped"] >= 3,
              f"mapped={suggestion['mapped']}")
        check("quantities are aggregated per task",
              any(m.get("quantity") == 200 for m in suggestion["mappings"]),
              json.dumps([(m["code"], m.get("quantity")) for m in suggestion["mappings"]]))
        check("elements with no matching task are reported",
              suggestion["unmatchedElements"] >= 1, f"{suggestion['unmatchedElements']}")

        stored = client.call("bim_link", handle=handle, op="set",
                             elementsPath=str(elements_path), codeField="Text1")
        check("the mapping is stored in a sidecar, not in the schedule",
              Path(stored["storePath"]).exists() and Path(stored["storePath"]).suffix == ".json",
              stored.get("storePath", ""))

        got = client.call("bim_link", handle=handle, op="get")
        check("the stored mapping reads back", got["mapped"] == stored["mapped"])

        pull = client.call("bim_sync", handle=handle, direction="model_to_schedule",
                           productivity={"m2": 40, "kg": 500, "m3": 15})
        check("model -> schedule yields duration proposals", pull["rows"] >= 3, f"rows={pull['rows']}")
        check("proposals are proposals, not writes",
              any("proposal" in n.lower() for n in pull["notes"]))

        fourd_path = workdir / "4d.csv"
        push = client.call("bim_sync", handle=handle, direction="schedule_to_model",
                           outputPath=str(fourd_path), statusDate="2026-09-15")
        check("schedule -> model writes one row per element",
              Path(fourd_path).exists() and push["rows"] >= 4, f"rows={push['rows']}")
        fourd = fourd_path.read_text(encoding="utf-8")
        check("the 4D rows carry element id, dates and status",
              all(c in fourd.splitlines()[0] for c in ("elementId", "plannedStart", "status")))

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
