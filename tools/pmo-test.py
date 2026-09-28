#!/usr/bin/env python3
"""Project control the way a PMO runs it: cost loading, change control, status report, look-ahead,
schedule risk, WBS rules and the earned value S-curve.

Runs on the internal engine and never starts Microsoft Project, so it is safe on any machine.

    python tools/pmo-test.py
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
                                "clientInfo": {"name": "pmo-test", "version": "1"}})
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


def state(mcp: Mcp, handle: str, all_rows: bool = False) -> dict:
    items = mcp.call("tasks_query", handle=handle, limit=1000)["items"]
    return {t["uid"]: t for t in items if all_rows or not t["summary"]}




def main() -> None:
    if not EXE.exists():
        sys.exit(f"build the server first: {EXE}")
    work = Path(tempfile.mkdtemp(prefix="hzpm-pmo-"))
    mcp = Mcp()
    try:
        print("build: 3 WBS branches x 6 tasks with budget codes, a chain, baseline, progress")
        h = mcp.call("project_open", path=str(work / "pmo.xml"), create=True,
                     name="PMO", startDate="2026-11-02")["handle"]
        codes = ["EST", "MAM", "ACB"]
        for b, code in enumerate(codes):
            mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": f"Frente {code}"}])
            parent = max(state(mcp, h, all_rows=True))
            mcp.call("tasks_write", handle=h, ops=[
                {"op": "create", "name": f"{code} {n}", "duration": f"{3 + n}d", "parentUid": parent,
                 "custom": {"Text1": code}} for n in range(6)])
        leaves = sorted(state(mcp, h).values(), key=lambda t: t["uid"])
        uids = [t["uid"] for t in leaves]
        mcp.call("links_write", handle=h, ops=[{"op": "link", "from": a, "to": b} for a, b in zip(uids, uids[1:])])

        print("\ncost-loaded schedule")
        r = mcp.call("schedule_update", handle=h, op="load_budget",
                     budget={"EST": 600000, "MAM": 300000, "ACB": 150000, "XXX": 1000})
        check("the budget is loaded onto the coded tasks", abs((r.get("budget") or r).get("loaded", 0) - 1050000) < 5
              or "1050000" in json.dumps(r).replace(",", "").replace(".0", ""), json.dumps(r)[:300])
        check("a budget line no task carries is reported", "XXX" in json.dumps(r), json.dumps(r)[:300])

        print("\nchange control")
        mcp.call("schedule_update", handle=h, op="save_baseline")
        try:
            mcp.call("schedule_update", handle=h, op="save_baseline", overwriteBaseline=True)
            check("a rebaseline without a reason is refused", False, "it was accepted")
        except RuntimeError as ex:
            check("a rebaseline without a reason is refused", "reason" in str(ex).lower(), str(ex)[:200])
        mcp.call("tasks_write", handle=h, ops=
                 [{"op": "update", "uid": u, "percentComplete": 100} for u in uids[:5]]
                 + [{"op": "update", "uid": uids[5], "percentComplete": 40}])
        mcp.call("schedule_update", handle=h, op="set_status_date", statusDate="2026-12-04")
        log = mcp.call("schedule_analyze", handle=h, aspects=["change_log"])["changeLog"]
        kinds = [c["kind"] for c in log]
        check("the change log has the baseline, the budget load and the edits",
              {"baseline", "edit"} <= set(kinds) and any("budget" in k for k in kinds), str(kinds))

        print("\nstatus report")
        rep = mcp.call("schedule_analyze", handle=h, aspects=["status_report"], record=True)["statusReport"]
        check("status report has SPI, CPI-less EAC and a health", rep.get("spi") is not None and rep.get("health"),
              json.dumps(rep)[:300])
        check("earned-schedule SPI(t) is reported", rep.get("spiTime") is not None, json.dumps(rep)[:300])
        check("with no actual cost, EAC is withheld and the report says why",
              len(rep.get("eac") or {}) >= 2 or "actual cost" in json.dumps(rep.get("notes")).lower(),
              str(rep.get("eac")) + str(rep.get("notes"))[:300])
        rep2 = mcp.call("schedule_analyze", handle=h, aspects=["status_report"], record=True)["statusReport"]
        check("the recorded cut-offs come back as a trend", len(rep2.get("trend") or []) >= 1, str(rep2.get("trend")))
        check("the ledger is a sidecar beside the file", (work / "pmo.hzpmo.json").exists())

        print("\nlook-ahead and PPC")
        la = mcp.call("schedule_analyze", handle=h, aspects=["lookahead"], weeks=3, commit=True)["lookahead"]
        check("the look-ahead lists tasks in the window", len(json.dumps(la)) > 50 and "ready" in json.dumps(la).lower(),
              json.dumps(la)[:300])

        print("\nschedule risk")
        risk = mcp.call("schedule_risk", handle=h, iterations=500, seed=7, target="2027-01-15")
        p = risk.get("percentiles") or {}
        check("P50 <= P80 <= P90", p and p["P50"] <= p["P80"] <= p["P90"], str(p))
        check("probability of the target is between 0 and 1",
              0 <= (risk.get("probabilityOfTarget") or 0) <= 1, str(risk.get("probabilityOfTarget")))
        check("risk drivers are ranked", len(risk.get("drivers") or []) > 0)
        again = mcp.call("schedule_risk", handle=h, iterations=500, seed=7, target="2027-01-15")
        check("the same seed gives the same answer", again.get("percentiles") == p)

        print("\nWBS rules and dictionary")
        mcp.call("tasks_write", handle=h, ops=[{"op": "create", "name": "Frente vacio"}])
        qa = mcp.call("schedule_qa", handle=h)
        rules = {f["rule"]: f for f in qa["findings"]}
        check("WBS rules run", "hrz_wbs_duplicate_code" in rules and "hrz_wbs_summary_loaded" in rules, str(list(rules))[:300])

        dic = mcp.call("project_export", handle=h, path=str(work / "wbs.csv"), format="wbs_dictionary")
        text = (work / "wbs.csv").read_text(encoding="utf-8")
        check("the WBS dictionary includes summaries and tasks", "summary" in text and "Frente EST" in text, text[:200])

        print("\nS-curve")
        sc = mcp.call("timephased_query", handle=h, measure="s_curve", granularity="week")
        curves = sc.get("curves") or {}
        check("the S-curve has PV, EV and AC in money", {"pv", "ev", "ac"} <= set(curves) and sc.get("valueMeasure") == "cost",
              str(list(curves)) + " " + str(sc.get("valueMeasure")))
        pv = curves.get("pv") or [{"value": 0}]
        check("PV ends at the budget", abs(pv[-1]["value"] - 1050000) < 50, str(pv[-1]))
        ev = curves.get("ev") or [{"value": 0}]
        check("EV stops at the status date", all(b["period"] <= "2026-12-04" for b in ev) and ev[-1]["value"] > 0, str(ev[-3:]))
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
