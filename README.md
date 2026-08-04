# Horizun Project MCP

[![build and test](https://github.com/HorizunGroup/horizun-msproject-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/HorizunGroup/horizun-msproject-mcp/actions/workflows/ci.yml)

**An MCP server for Microsoft Project that needs neither Java nor Microsoft Project — and tells you
the truth about what it wrote.**

<!-- mcp-name: io.github.HorizunGroup/horizun-msproject-mcp -->
<!-- The registry verifies ownership by finding that name in the published package's README.
     It has to travel inside the NuGet package, which is why it lives here rather than in a
     metadata file. -->


Point any MCP client at a `.mpp`, a Primavera `.xer`, or an MSPDI `.xml` and ask real questions:
where the critical path runs, which resources are overbooked, whether the schedule would survive a
DCMA audit, what a two-week slip actually does to the finish date. Then write changes back — and
know which ones landed, because every one is re-read from the model before it is reported.

```bash
dotnet tool install -g HorizunMsProjectMcp
```

That is the whole install. No JVM. No Microsoft Project. No licence. It reads and schedules on
its own.

[![NuGet](https://img.shields.io/nuget/v/HorizunMsProjectMcp.svg)](https://www.nuget.org/packages/HorizunMsProjectMcp)

---

## Why this one

There are a handful of Microsoft Project MCP servers. Every one of them requires Java, or a
licensed Microsoft Project install, or a paid JDBC driver — and on the machine this was built on,
**none of them would start**. Seven things here are not available anywhere else:

| | |
|---|---|
| **Zero prerequisites** | A single .NET binary. MPXJ is compiled to .NET through IKVM, so there is no JVM anywhere in the picture, and no Microsoft Project either. |
| **Verified writes** | Microsoft Project silently ignores writes all the time — auto-scheduled dates, hard constraints, summary rollups, calculated costs. Nothing here is reported as applied until it has been read back out of the model and matched. |
| **DCMA 14-point assessment** | The industry standard for judging whether a schedule can be run on. The Primavera servers implement it; none of the Microsoft Project ones do. Check 12 genuinely injects a 600-day delay and measures what moves. |
| **It recovers logic nobody linked** | A schedule laid out correctly on the bar chart but never linked is the most common defect there is: DCMA flags it, nothing fixes it. The dates already state the order — this reads it back out, verifies the same order holds across every repetition, and proposes the missing links. |
| **Reprogramming that is measured** | Recovery options are applied to a copy of the schedule, rescheduled, and reported with the finish date they actually produce. An option that recovers nothing says so instead of being offered as advice. |
| **It learns from your past projects** | Point it at finished schedules and it reports what each activity really took and what usually precedes it, then drafts a new programme from that — one task per unit, sequenced the way the trades actually followed each other. |
| **A BIM bridge** | Tie schedule tasks to model elements by code, turn measured quantities into duration proposals, and emit the per-element dates that drive 4D in Navisworks and the progress dashboard in Power BI. Nobody else does this at all. |

---

## The 25 tools

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
full earned value (BCWS/BCWP/ACWP, SPI, CPI, EAC, TCPI) — denominated in cost where the schedule
carries costs, in work hours where it carries hours, and weighted by duration where it carries
neither, which is most of them. The report says which.

**Writing** — `tasks_write` · `links_write` · `resources_write` · `calendars_write` · `schedule_update`

Batched, typed, verified. Cycles are refused before they are applied, with the offending chain named.
Two things this backend cannot do are not offered: reordering a task within the outline, and editing
a calendar's weekly working-hours pattern. Asking for either gets a refusal that names it and says
where to do it instead — an operation that half-works is worse than one that is absent.

**Planning** — `schedule_recovery` · `schedule_target` · `schedule_sequence` · `schedule_learn` · `schedule_generate`

Reprogramming, measured rather than asserted. `schedule_recovery` finds what is late, ranks it by
how much of the schedule sits behind it, then tries each recovery lever — removing lag on the
driving chain, overlapping hand-offs, compressing the longest critical tasks — on a throwaway copy
and reports the finish date each one genuinely produces. `schedule_target` tests a date you have
been handed and names the work the network does not hold in place. `schedule_sequence` recovers the
logic a schedule is missing by reading the order its own dates already state — the planner laid the
work out correctly and never linked it, and that decision is recoverable. `schedule_learn` mines finished
schedules for how long each activity actually takes and what usually comes before it;
`schedule_generate` turns that into a first draft, one task per apartment or floor, sequenced the
way the history says the trades follow each other.

Feed `schedule_learn` a model export alongside the schedules and it measures **productivity** —
what a crew actually got through in a day — by joining quantities to tasks on the shared code.
`schedule_generate` then sizes durations from the quantities of the new project rather than
copying a remembered duration, because the rate is what carries between projects and the quantity
is what changes. Give one export per schedule, in the same order: rates are measured per project,
and quantities totalled across projects would inflate every one of them.

A draft can only be as well sequenced as the schedules it learned from. Where the sources link each
activity to itself unit after unit but never to the trades around it, both tools say so and name the
number: the library reports how many activities learned a predecessor other than themselves, and the
draft reports how many trades it left with nothing scheduled before them.

**Interop** — `project_export` · `project_import` · `bim_link` · `bim_sync`

CSV, JSON, MSPDI, Primavera XER and PMXML, native `.mpp`, and a shaped Power BI dataset. Imports
plan before they write.

### What it costs to have loaded

The 25 tools present about **8,400 tokens** of schema, in every prompt, for as long as the server is
connected. That is the honest price of the surface and it is worth knowing before choosing to carry
it. It is also why the surface stayed at 25: the largest alternative ships 79 tools, and past a
point an agent cannot hold the surface in its head well enough to choose correctly within it.

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

Saving over a baseline that already holds data is refused too, unless you ask for it explicitly.
A baseline is the record of the original plan that every variance is measured against, and it
cannot be recovered from the file afterwards.

Everything else — scheduling, recalculation, dry-run simulation, rescheduling incomplete work, the
DCMA Critical Path Test — is served by this server's own critical-path engine and works on both
backends.

## The critical-path engine — and what it is not

MPXJ reads and writes schedule files but does not *schedule* them: a task created through it has no
dates at all. So there is a real CPM engine here — forward pass, backward pass, total and free
float, critical flags — honouring relationship types, lag, constraints, deadlines, actual dates, and
the working calendar per task (six-day site weeks, night shifts, exceptions you add with
`calendars_write`). Without it, dates, float, earned value and every impact figure would be empty on
any schedule this server authored.

> ### ⚠️ It is not Microsoft Project's scheduler
>
> **This engine reproduces Microsoft Project exactly on schedules built through this server. It does
> not reproduce it on real imported schedules.** Measured against four production construction
> files, recalculating reproduced Microsoft Project's own start dates on 100% of tasks in one
> schedule, 69% in another, and around 23% in two more — where most of the remainder moved by a week
> or more.
>
> Microsoft Project's scheduler has behaviours this engine does not implement: task types
> (fixed units, duration or work), effort-driven scheduling, resource calendars driving dates,
> manually scheduled tasks, split tasks, and elapsed durations. On a schedule that uses them, our
> dates will differ.
>
> **So imported schedules are never silently rescheduled.** Open a `.mpp` and its dates stay exactly
> as Microsoft Project computed them; a write reports what it changed and says plainly that dates
> were not recalculated. If you want this engine's dates instead, ask for them explicitly with
> `schedule_update op='recalculate'` — which warns you first, and after which the document is ours
> rather than Project's.
>
> Reading, querying, analysis, DCMA-14 and earned value all run on Microsoft Project's own dates and
> are unaffected. A dry run measures its impact against this engine on both sides, so the movement
> it reports is caused by your change rather than by the two engines disagreeing.
>
> If Microsoft Project is installed, `project_health` reports the COM backend and you can hand the
> file back to Project itself for a native save.

---

## Wiring it to a client

Claude Desktop, Claude Code, Cursor, VS Code — anything that speaks MCP over stdio:

```jsonc
{
  "mcpServers": {
    "horizun-msproject": {
      "command": "horizun-msproject-mcp"
    }
  }
}
```

Registry name: `io.github.horizungroup/horizun-msproject-mcp` (see [`server.json`](server.json)).

Call `project_health` first in every session — it tells you which backend you are on and what it
can do.

## Build and test from source

```bash
cd src/HorizunMsProjectMcp && dotnet build && cd ../..
python tools/acceptance-test.py   # 65 checks, all 20 tools end to end
python tools/scheduler-test.py    # 45 checks, critical-path engine correctness
python tools/planning-test.py     # 52 checks, reprogramming and learning
python tools/robustness-test.py   # 34 checks, concurrency and hostile input
python tools/smoke-test.py        # 13 checks, environment and capabilities
```

**209 checks**, driven over real JSON-RPC against the running server, on Windows and on
Linux. The Linux job is the evidence for the headline claim: it runs on a machine with no
JVM and no Microsoft Project.

The acceptance suite builds a construction schedule from nothing and asserts the contracts above:
that a dry run commits nothing, that a cycle is refused before it is applied, that a write to an
unknown uid is rejected rather than ignored, and that the DCMA Critical Path Test moves the finish
date by exactly the delay injected into it.

The scheduler suite is the one that earns trust in the dates. It covers start-to-start,
finish-to-finish and start-to-finish logic, positive and negative lag, hard and soft constraints,
deadlines producing negative float, calendar exceptions actually pushing the schedule out, and a
WBS hierarchy with summary rollup, that an imported schedule is never silently rescheduled, and
full round trips through Primavera XER and PMXML and through a real binary `.mpp` — the last
written by Microsoft Project itself, read back by MPXJ, with dates, milestone flags, budget codes
and dependencies all intact.

Beyond the suites, the server has been driven through a planner's full working cycle on two
production construction schedules — a 5,985-task programme and a 170 MB, 1,937-task one — opening,
auditing, baselining, recording progress, measuring earned value and exporting the Power BI
dataset, and read against files of up to 7,000 tasks.

The robustness suite is the one that matters for trusting this in a real client: forty writes in
flight at once on the same document, hostile paths, absurd page sizes, non-latin names. MPXJ's
object model is not thread-safe and an MCP client is free to pipeline calls — unguarded, sixty
concurrent writes all failed and left the document unusable. Every document now has its own lock,
reentrant because several tools are built on others.

A schedule stays in memory until it is closed, and a 170 MB one costs around 400 MB. Reading many
retires the oldest document that has nothing unsaved; when every open document has unsaved work,
opening another is refused rather than discarding any of it. `project_health` reports what is open
and what it is costing.

Rebuild the installable package with `dotnet pack -c Release`.

---

## Formats

**Reads** `.mpp` `.mpt` `.mpx` MSPDI `.xml` · Primavera `.xer` `.pmxml` · Asta `.pp` · Planner ·
GanttProject and more, through [MPXJ](https://www.mpxj.org/).

**Writes** MSPDI `.xml` (Microsoft Project opens it natively) · `.mpx` · Primavera `.xer` and
`.pmxml` · Planner · SDEF · JSON · CSV · Power BI dataset · native `.mpp` where Microsoft Project
is installed.

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
  acceptance-test.py   end-to-end across all 20 tools, 65 checks
  scheduler-test.py    engine correctness, format round trips, safety guards, 45 checks
  planning-test.py     recovery, sequencing, target dates, learning, generation, 52 checks
  robustness-test.py   concurrency, malformed input, resource limits, 34 checks
  smoke-test.py        environment and capability matrix, 13 checks
```

Design rationale and the market benchmark that motivated it: [DESIGN-TOOL-SURFACE.md](DESIGN-TOOL-SURFACE.md)
and [BENCHMARK-MCP-MSPROJECT.md](BENCHMARK-MCP-MSPROJECT.md).

## Licence

MIT.
