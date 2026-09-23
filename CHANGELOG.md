# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

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

[1.2.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.2.0
[1.1.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.1.0
[1.0.2]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.0
