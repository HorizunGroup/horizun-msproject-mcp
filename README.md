# Horizun Project MCP

**An MCP server for Microsoft Project that needs neither Java nor Microsoft Project — and tells you
the truth about what it wrote.**

Point any MCP client at a `.mpp`, a Primavera `.xer`, or an MSPDI `.xml` and ask real questions:
where the critical path runs, which resources are overbooked, whether the schedule would survive a
DCMA audit, what a two-week slip actually does to the finish date. Then write changes back — and
know which ones landed, because every one is re-read from the model before it is reported.

```bash
dotnet tool install -g HorizunMsProjectMcp
```

That is the whole install. No JVM. No Microsoft Project. No licence. It reads and schedules on
its own.

*(Not on NuGet.org yet — until then, build the package with `dotnet pack -c Release` and install it
with `dotnet tool install -g --add-source ./nupkg HorizunMsProjectMcp`.)*

---

## Why this one

There are a handful of Microsoft Project MCP servers. Every one of them requires Java, or a
licensed Microsoft Project install, or a paid JDBC driver — and on the machine this was built on,
**none of them would start**. Four things here are not available anywhere else:

| | |
|---|---|
| **Zero prerequisites** | A single .NET binary. MPXJ is compiled to .NET through IKVM, so there is no JVM anywhere in the picture, and no Microsoft Project either. |
| **Verified writes** | Microsoft Project silently ignores writes all the time — auto-scheduled dates, hard constraints, summary rollups, calculated costs. Nothing here is reported as applied until it has been read back out of the model and matched. |
| **DCMA 14-point assessment** | The industry standard for judging whether a schedule can be run on. The Primavera servers implement it; none of the Microsoft Project ones do. Check 12 genuinely injects a 600-day delay and measures what moves. |
| **A BIM bridge** | Tie schedule tasks to model elements by code, turn measured quantities into duration proposals, and emit the per-element dates that drive 4D in Navisworks and the progress dashboard in Power BI. Nobody else does this at all. |

---

## The 20 tools

**Session** — `project_health` · `project_open` · `project_save`

`project_health` is a doctor, not a ping. It detects Microsoft Project, tests whether its COM server
*actually starts*, and when it does not, hands back the HRESULT diagnosis and the repair steps. That
is not hypothetical: this machine hit `CO_E_SERVER_EXEC_FAILURE` with a perfectly valid registration,
and step one of the repair path it emits is what fixed it.

**Reading** — `project_info` · `tasks_query` · `links_query` · `resources_query` · `timephased_query`

Filtered, paged, field-selectable. Tasks are addressed by their stable `uid`; the row `id` is display
only, because it shifts the moment a task is inserted and addressing by it edits the wrong task.

**Analysis** — `schedule_analyze` · `schedule_qa` · `baseline_compare`

Computed server-side, so the agent asks a question instead of pulling two thousand tasks into
context. Critical path, float distribution, driving path, day-by-day overallocation, DCMA-14, and
full earned value (BCWS/BCWP/ACWP, SPI, CPI, EAC, TCPI).

**Writing** — `tasks_write` · `links_write` · `resources_write` · `calendars_write` · `schedule_update`

Batched, typed, verified. Cycles are refused before they are applied, with the offending chain named.

**Interop** — `project_export` · `project_import` · `bim_link` · `bim_sync`

CSV, JSON, MSPDI, native `.mpp`, and a shaped Power BI dataset. Imports plan before they write.

---

## The two contracts

**Nothing is applied until it is verified.**

```jsonc
{
  "applied": 12,              // operations that fully succeeded, re-read from the model
  "fieldsVerified": 31,
  "rejected": [{
    "uid": 45, "field": "finish",
    "requested": "2026-09-10", "actual": "2026-09-14",
    "reason": "'finish' is calculated from the task's duration and its predecessors. Change the
               duration or the logic, or set a constraint, rather than writing the date."
  }],
  "impact": {
    "tasksMoved": 312,
    "projectFinishBefore": "2027-03-14", "projectFinishAfter": "2027-03-28",
    "criticalPathChanged": true, "newNegativeFloat": 8
  },
  "verifiedBy": "reread"
}
```

**A dry run is a real simulation.** `dryRun: true` deep-copies the schedule, applies the batch,
reschedules it with the critical-path engine, measures the difference, and throws the copy away.
The impact numbers are observed, not predicted.

---

## Capability honesty

`project_health` publishes a capability matrix, and the tools honour it. Two things stay `false` on
the file backend and **refuse rather than approximate**:

- **`write_native_mpp`** — no library can author the binary format. Where Microsoft Project is
  installed, the save is delegated to it and you get a genuine `.mpp`; where it is not, you get
  MSPDI and an explanation.
- **`level_resources`** — Microsoft Project's levelling heuristic is unpublished. Any imitation
  would be a different answer wearing the same name.

Everything else — scheduling, recalculation, dry-run simulation, rescheduling incomplete work, the
DCMA Critical Path Test — is served by this server's own critical-path engine and works on both
backends.

## The critical-path engine

MPXJ reads and writes schedule files but does not *schedule* them: a task created through it has no
dates at all. So there is a real CPM engine here — forward pass, backward pass, total and free
float, critical flags — honouring relationship types, lag, constraints, deadlines, actual dates, and
the working calendar (including exceptions you add with `calendars_write`). Without it, dates,
float, earned value and every impact figure would be empty on any schedule this server authored.

---

## Wiring it to a client

```jsonc
{
  "mcpServers": {
    "horizun-project": { "command": "horizun-msproject-mcp" }
  }
}
```

Call `project_health` first in every session — it tells you which backend you are on and what it
can do.

## Build and test from source

```bash
cd src/HorizunMsProjectMcp && dotnet build && cd ../..
python tools/acceptance-test.py   # 57 checks, all 20 tools end to end
python tools/scheduler-test.py    # 22 checks, critical-path engine correctness
python tools/smoke-test.py        # 13 checks, environment and capabilities
```

**92 checks**, driven over real JSON-RPC against the running server.

The acceptance suite builds a construction schedule from nothing and asserts the contracts above:
that a dry run commits nothing, that a cycle is refused before it is applied, that a write to an
unknown uid is rejected rather than ignored, and that the DCMA Critical Path Test moves the finish
date by exactly the delay injected into it.

The scheduler suite is the one that earns trust in the dates. It covers start-to-start,
finish-to-finish and start-to-finish logic, positive and negative lag, hard and soft constraints,
deadlines producing negative float, calendar exceptions actually pushing the schedule out, and a
full round trip through a real binary `.mpp` — written by Microsoft Project, read back by MPXJ,
with dates, milestone flags, budget codes and dependencies all intact.

Rebuild the installable package with `dotnet pack -c Release`.

---

## Formats

**Reads** `.mpp` `.mpt` `.mpx` MSPDI `.xml` · Primavera `.xer` `.pmxml` · Asta `.pp` · Planner ·
GanttProject and more, through [MPXJ](https://www.mpxj.org/).

**Writes** MSPDI `.xml` (Microsoft Project opens it natively) · `.mpx` · JSON · CSV · Power BI
dataset · native `.mpp` where Microsoft Project is installed.

## Layout

```
src/HorizunMsProjectMcp/
  Diagnostics/   COM detection and the environment doctor
  Backends/      MPXJ I/O, the COM bridge, sessions and fingerprints
  Analysis/      critical-path engine, working calendar, DCMA-14, earned value
  Writes/        the verified-write engine
  Bim/           element matching and the 4D bridge
  Tools/         the 20 MCP tools
tools/
  acceptance-test.py   end-to-end across all 20 tools, 57 checks
  scheduler-test.py    critical-path engine correctness + .mpp round trip, 22 checks
  smoke-test.py        environment and capability matrix, 13 checks
```

Design rationale and the market benchmark that motivated it: [DESIGN-TOOL-SURFACE.md](DESIGN-TOOL-SURFACE.md)
and [BENCHMARK-MCP-MSPROJECT.md](BENCHMARK-MCP-MSPROJECT.md).

## Licence

MIT.
