# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.4.1] - 2026-09-28

1.4.0 run against Microsoft Project itself (16, Spanish), on a copy of the real schedule and on the
engine suite. What it found:

### Fixed

- **A crew's units were lost on the way to Project.** MPXJ sizes a new assignment's work from the
  task's duration alone, and Project believes the work: a crew assigned at 200% worked at 100%, and
  nothing could be found overallocated, let alone levelled. Work is now duration × units.
- **A material's quantity came back wrong**: 15 bags of cement on a 3-day task were read by Project
  as 24 (MPXJ wrote the duration as the work, and the quantity divided by 100). The work of a
  material is now its quantity.
- **Levelling moved nothing.** Three things stopped Project, each measured: MPXJ writes "Can level"
  and "Level assignments" as No when it never read them (every resource and task in every .mpp
  written through it came back with levelling off), and stale peak units and spans on each resource.
  Unknown flags now go over as Project's default, Yes, and the derived figures are left for Project
  to compute. `level_resources` is now available wherever Project calculates, not only on the old
  COM backend.
- An export over a read-write document's own file is a save, and is allowed again; only a read-only
  document's file or another open document's is refused (the 1.4.0 guard was too broad).

### Verified against Microsoft Project

- `project-engine-test.py` gains a build-and-export section: an outline with a task added later under
  an earlier summary, 'Peón' at 400%, a material by quantity, a Saturday half-day, levelling, and the
  .mpp read back — 29 checks, all passing with Project 16. Acceptance (65) and scheduler (45) pass
  with Project as the engine.
- On a copy of the real schedule: outline levels and WBS, 'Peón' keeping its work at 400% with
  nothing moved to 'Electricista', max units, the project calendar's Saturday, task calendar, fixed
  and baseline cost — all kept in the .mpp Project wrote.
- Still not exercised: the retry after 0x80080005, which needs the failure to be provoked.

## [1.4.0] - 2026-09-28

A field report from building and running a real construction schedule end to end — baseline,
progress, costs, earned value, recovery, baseline 1 — on an .mpp Project saved, with a Spanish
Microsoft Project. Everything that corrupted data, misled, or forced a switch to driving Project by
hand.

### Fixed — data integrity

- **Tasks added under a summary landed a level too high in the .mpp, and every WBS read "0".**
  MPXJ gives what is added to a file it read no outline level, outline number or unique id, and keeps
  rows in the order they were added: a task added under an earlier summary after later rows existed
  was written after them, and Project hung it under the wrong summary. Rows are now numbered in
  outline order, the hierarchy rebuilt from row order and level as Project reads it, and outline
  numbers and WBS follow (a WBS the schedule chose is kept). An `outline` indent now also makes the
  row above a summary, so summaries stop being analysed as detail tasks.
- **A resource created on an imported file was rejected, left behind, and broke every save.** Neither
  it nor its calendar got a unique id; `project_save` and `project_export` then failed with "Object
  reference not set". Resources, calendars, tasks and assignments now always get ids, and a rejected
  resource create is rolled back.
- **`tasks_write` `custom` wrote every key into a text field** (Number1 into Text1, WBS into Text1).
  It now writes Text1-30, Number1-20, Date1-10, Flag1-20 and WBS to themselves, and rejects any other
  key by name.
- **An export could write over the file of an open schedule — a read-only one included.**
  `project_export` now refuses any path that is a schedule open in this server; saving the original
  is `project_save`'s job, which already refused read-only handles.
- **The .mpp export said "Verified" over a broken outline and wrong resources.** Reading the file
  back now also compares outline level and WBS, who is assigned to each task (by name) and at what
  units, task and project calendars, fixed and baseline cost, and each resource's max units, rate and
  calendar. A difference fails the export and leaves the target untouched.
- **`maxUnits` changed nothing Project would see**, and `standardRate` was accepted and dropped.
  Max units now go into the availability table as well as the field, rates into the cost rate table.

### Fixed — readings that misled

- **Max units read as 100** for crews Project shows at 600/400/800 %: MPXJ answers from the
  availability row covering today. They now come from the availability table.
- **Overallocation summed whole days**, so two tasks one after the other on the same day counted as
  working together. It now takes the most units at work at the same moment, on the project
  calendar's working days (Saturdays included on a six-day calendar).
- **A status date given as a day was read as 00:00**, so work reported at 10:00 that day was "in the
  future" (DCMA 09). It now means the whole day.
- **The S-curve skipped periods with nothing planned**, and spread value over Monday-Friday whatever
  the calendar. Every period is present and the project calendar's working days are used.
- **New tasks were dated 08:00-17:00 whatever the calendar**, and a milestone spanned the whole day.
  They take the calendar's own hours, and a milestone is a moment.
- **Work handed to Project moved to the locale's "Estándar" hours (9-13, 15-19)**: Project lays
  placeholder work on the unassigned resource's calendar, which derives from the locale template. In
  the hand-off it now derives from the project calendar.
- **`schedule_recovery` could return no options and say nothing.** Each lever dropped is now named
  with the reason; with nothing marked critical, the least-float chain is used.
- **Assigning a resource or material to a finished task reopened it** (99%, remaining work pushed past
  the status date — +16 days on the real schedule). The assignment is created finished.
- `level_resources` was listed but not implemented.
- COM: a start refused with 0x80080005 after a Project closed by force is retried, then explained;
  a Project on its start screen or behind a dialog is named as the cause; an .mpp export that failed
  on a broken model reports the cause instead of "An error occurred invoking".

### Added

- `calendars_write`: `set_week` (working days and hours: half or full Saturdays, shifts, in English
  or Spanish day names), working exceptions with hours, `create` with `basedOn` (a base calendar copying another's week
  and exceptions),
  `set_project_calendar`.
- `tasks_write`: `calendar` (a base calendar; a resource's own is refused), `ignoreResourceCalendar`, `taskType`, `effortDriven`, `fixedCost`,
  `fixedCostAccrual`, `baselineCost` into any `baselineSlot` without touching baseline dates,
  `actualStart: "none"` to return a task to not started, and `key`/`parentKey` to build a whole
  outline in one batch.
- `resources_write`: `overtimeRate`, `materialLabel` (materials assigned by quantity), `calendar`.
- `schedule_update` op `level_resources`, through Microsoft Project's leveller, with `withinSlack`
  and `levelingCanSplit`.
- `schedule_scenarios` (new tool): what-if alternatives — calendar, resource, task and logic changes
  together — each on its own copy, side by side: finish, cost, overallocation, critical tasks.
- `schedule_recovery` levers: overlap once half the predecessor is done, full Saturdays, and all
  useful levers combined, measured rather than summed.
- `links_write` lags in elapsed time (`3ed`, `2ew`) and as a percentage of the predecessor (`50%`).
- `schedule_qa`: `justifiedLags` leaves justified lags and leads out of checks 2 and 3; new rule
  `hrz_finished_incomplete` (actual finish but under 100%).
- `schedule_analyze` `lookahead`: `ownerField` and `constraintsField` for who answers for each task
  and the constraints logic cannot see (material, permits, equipment).
- `status_report`: bottom-up EAC (actual plus the remaining cost as scheduled) and the currency.
- `timephased_query` `s_curve`: a `forecast` curve (to date, then remaining work to the projected
  finish); `project_export` format `scurve_xlsx`: the S-curve as a workbook with a native chart.
- `project_info`: the project calendar, its working week and the currency; `resources_query`:
  overtime rate, material unit and calendar.
- `tools/imported-file-test.py` (16 checks) and `tools/planner-features-test.py` (32 checks), internal
  engine only.

### Not verified against Microsoft Project

Verified in 1.4.1, which fixes what that turned up.

## [1.3.0] - 2026-09-27

It now works as a PMO, not only as a Project operator.

### Added

- `schedule_risk` (new tool): Monte Carlo schedule risk analysis over the network's own logic.
  P10/P50/P80/P90 finish, probability of a target and of the baseline, P80 contingency, risk drivers
  by criticality and sensitivity, histogram; triangular or PERT; seeded runs repeat.
- `schedule_analyze` aspect `status_report`: planned vs earned, SPI, CPI, earned-schedule SPI(t),
  EAC by three methods, TCPI, forecast finish, milestones at risk, top issues, health, and the trend
  of recorded cut-offs (`record=true`).
- `schedule_analyze` aspect `lookahead`: short-interval (Last Planner) plan with constraints, ready vs
  constrained, weekly commitments (`commit=true`) and PPC.
- Formal change control and `schedule_analyze` aspect `change_log`: a rebaseline over an existing
  baseline requires `reason` (and takes `approvedBy`); baselines, budget loads and committed edit
  batches are logged with their finish impact in `<name>.hzpmo.json` beside the schedule.
- `schedule_update` op `load_budget`: a budget by code (inline or CSV) loaded onto the tasks as cost,
  reporting budget lines no task carries and tasks left without budget.
- `timephased_query` measure `s_curve`: cumulative PV, EV and AC, in money when cost-loaded.
- WBS rules in `schedule_qa` (100% rule) and `project_export` format `wbs_dictionary`.
- `schedule_generate` builds a schedule from Revit elements passed inline; `schedule_recovery`
  returns a planner's replan review.
- `tools/pmo-test.py`: 19 checks over the control layer, internal engine only.

### Fixed

- **Exporting to .mpp lost progress.** A 215-task MSPDI with 41 finished and 2 in progress came back
  from `project_export(format="mpp")` with 1 finished and none in progress, while the export
  reported success. The loss happened when writing, not reading: Project derives progress from
  assignment work when it imports MSPDI, and the hand-off leaves out started tasks' placeholder
  assignments (they are what put their dates on the wrong calendar). Progress and the status date
  are now set back through Project's own fields before saving.
- **Attaching to a running Microsoft Project crashed the server.** `Activator.CreateInstance` against
  an open Project was measured to kill the .NET runtime outright (Internal CLR error 0x80131506,
  reproducible from bare PowerShell) — the server died with no error. It now takes the running
  instance from the running-object table, as other COM clients do, and only creates one when none runs.
- Progress is restored in the order Project honours, measured on a real 215-task schedule: finished
  tasks get actual finish then actual start; work in progress gets percent, then actual start, then
  percent again (Project ignores an actual start on a task at 0%).
- **A .mpp is only written if it reads back intact.** It is saved beside the target, reopened, and
  compared task by task — percent complete, actual start and finish, actual duration and work,
  baseline, status date. Only a matching file replaces the target; otherwise the export fails with
  what was lost and the target is left untouched. Durations and actual work that Project re-derives
  because the source states them inconsistently with its own dates are written, with a WARNING
  naming each task — never as a plain success.
- **Every other export says what it kept.** MSPDI reports "Verified"; MPX (baseline without times),
  XER (no baseline) and PMXML (no in-progress percent) report a WARNING naming the loss instead of
  plain success.
- `project_save` with `format="mpp"` wrote a binary .mpp named `.xml` beside the original while its
  description said the server could not write .mpp at all. It now saves the .mpp, over the original
  only once verified, and its description states what each path really does.

### Added

- **`schedule_generate` builds from the model.** Pass the elements read from the Revit MCP inline
  (`elements`: elementId, code, category, level, quantity, unit) — no export file — and get one task per
  trade per level in construction sequence, start and finish milestones with nothing open-ended,
  durations from `productivity` (by code, category or unit) or `defaultDays` marked as assumed, the
  code in `codeField` for 4D, and every element tied to its task so `bim_sync` writes the 4D file.
- **`schedule_recovery` reviews like a planner.** `replanReview` lists the tasks to re-plan at the
  cut-off with the reasons and figures (not started when due, overdue, slower than its elapsed time,
  behind baseline, negative float, started out of sequence) and the move to make.
- `tools/export-fidelity-test.py`: round trip of a baselined schedule with a status date and finished,
  in-progress and unstarted tasks, safe on any machine; the native .mpp round trip is in
  `project-engine-test.py`.

## [1.2.1] — 2026-09-22

### Fixed

- `project_health` reported its version as 0.1.0, a constant left from the first commit. It now reads
  the package version, and the smoke test fails if the two ever disagree again.

## [1.2.0] — 2026-09-22

### Changed

- **Microsoft Project calculates the dates wherever it is installed.** Recalculation,
  rescheduling after a write, dry runs, `schedule_recovery`, `schedule_target` and the DCMA
  Critical Path Test hand the schedule to Project and take back what it computes. On seven
  real construction schedules (8,823 tasks counting summaries) the result matches the dates
  Project computes on every task, to the minute, with progress unchanged; so do duration, percent-complete and link edits, compared
  task by task with the same edit made in Project. `project_health` reports the engine as
  `schedulingEngine`. The built-in engine remains for machines without Project, and
  `HORIZUN_MSPROJECT_ENGINE=internal|project|auto` overrides the choice.
- Imported schedules are rescheduled after a write when Project is the engine, the way Project
  itself does. Every applied write is verified again after Project calculates, and one Project
  undid is reported as rejected rather than applied.
- Progress follows Project's rules on edits: a new duration on a started task keeps the work
  already done, a new percent complete starts or finishes the task, and summaries roll progress
  up the way Project does.

### Fixed

- **Data loss with Microsoft Project open.** Project runs a single instance, so automation lands
  in the copy the user has open. Saving a native `.mpp` and `project_health deep=true` both hid
  that window and quit it without saving — discarding unsaved work. All automation now goes
  through one component that never hides, recalculates, closes or quits what the user has open,
  touches only its own temporary copy, and kills only an instance it started itself if a hidden
  dialog stalls it.
- **Saving a native `.mpp` changed durations.** The file handed to Project re-derived durations
  from placeholder assignments and dropped split tasks' gaps; on one real schedule every task
  came back on different dates. The `.mpp` now carries Project's dates unchanged.
- `schedule_update op='recalculate'` calculated the schedule twice.
- A resource created here had no calendar, so Project gave it its own locale default (9:00-13:00 and
  15:00-19:00 on a Spanish install) and every task it was assigned to moved to those hours. New
  resources now work the project's calendar, as they do when created in Project.
- Changing a duration left the task's assignments, its remaining duration and its summaries' progress
  as they were. Assignments are now resized by the task type (fixed units and duration keep the
  units; fixed work keeps the work), an unstarted task's remaining duration follows its duration, and
  progress rolls up to summaries the way Project does. Found by the DCMA Critical Path Test, whose
  600-day injection into a resourced task was being quietly sized back to the original 5 days.
- Automation is serialised across processes, not only threads: Claude Desktop and Claude Code each run
  their own server, and both can reach the one Microsoft Project a machine has.

### Added

- `tools/project-engine-test.py`: 20 checks holding the server to Microsoft Project by hand —
  a schedule built from nothing, and duration, percent-complete and link edits, compared task by task
  (dates, progress and summaries) — and checking that a Project the user has open is left exactly as
  it was. It skips where Project is not installed.
- `HORIZUN_MSPROJECT_COM_TIMEOUT_SECONDS` and `HORIZUN_MSPROJECT_DEBUG_DIR`; see docs/INSTALL.md.

## [1.1.0] — 2026-09-22

### Added

- **Claude Desktop extension.** Releases now carry a `.mcpb` bundle. It is about
  15 KB because it does not carry the server: on first launch it installs
  `HorizunMsProjectMcp` from NuGet and runs that. An already-installed copy is
  found and reused.
- **Client manifests** for Claude Code (`.claude-plugin/`) and Codex
  (`.codex-plugin/`), plus a marketplace entry, matching the layout of the other
  HorizunGroup MCP servers.
- `.mcp.json.example` and [docs/INSTALL.md](docs/INSTALL.md), which covers all
  four clients — including the part where ChatGPT needs an HTTPS bridge and why.
- `scripts/build_mcpb.py`, which refuses to pack when the version in the
  manifest, the launcher, the csproj and the registry manifest disagree, and
  fills the extension's advertised tool list by asking a built server what it
  actually exposes.
- `tools/packaging-test.py`: 24 checks over the packaging metadata.

### Changed

- `server.json` moved to `.mcp/server.json`, where the sibling repositories keep
  it. The registry workflow publishes from the new path.

## [1.0.2] — 2026-09-22

### Fixed

- Registry namespace casing: the MCP registry grants `io.github.HorizunGroup/*`
  with the organisation's exact spelling, and ownership is proved by the
  `mcp-name` marker inside the *published* package — so correcting the manifest
  alone was not enough and the package had to be rebuilt.
- `registryBaseUrl` now points at NuGet's v3 service index rather than the host.

## [1.0.1] — 2026-09-22

### Added

- Registry ownership marker in the README, carried inside the NuGet package.

## [1.0.0] — 2026-09-22

First public release. 25 tools, 209 automated checks.

- Critical-path engine with working calendars, constraints and all four relation
  types — no Java, no Microsoft Project.
- DCMA 14-point schedule assessment, earned value, baseline comparison.
- Verified writes: nothing is reported as applied until it has been re-read from
  the model and matched.
- Reprogramming that is measured against a throwaway copy rather than asserted.
- Logic recovery from a schedule's own dates, schedule learning from finished
  projects, and a BIM bridge for 4D.

[1.2.1]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.2.1
[1.2.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.2.0
[1.1.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.1.0
[1.0.2]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.0
