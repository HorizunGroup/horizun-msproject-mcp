# Contributing

Bug reports and pull requests are welcome. A few things about how this project works, so a change
lands cleanly.

## Build and test

```bash
cd src/HorizunMsProjectMcp && dotnet build && cd ../..
python tools/acceptance-test.py
python tools/scheduler-test.py
python tools/planning-test.py
python tools/robustness-test.py
python tools/smoke-test.py
python tools/packaging-test.py
```

The first build translates MPXJ from Java with IKVM and takes several minutes. Later builds do not.

`packaging-test.py` needs no build: it reads the metadata. Nine files repeat the version, the
package id and the command name, and nothing else catches them drifting — the server still starts,
every other suite still passes, and the only symptom is a client that installs one thing and runs
another.

The suites drive the real server over JSON-RPC on stdio — there are no mocks, and nothing is
asserted about the internals. If a change is worth making, it should be visible from outside.

## The contracts a change has to keep

These are not style preferences; several were written after a bug proved they were needed.

**Nothing is reported as applied unless it was re-read from the model.** Not "the call did not
throw" — read back and compared. Microsoft Project silently ignores writes constantly, and a tool
that reports success it did not verify is worse than one that fails.

**A capability the backend cannot honour is refused, never approximated.** `project_health`
publishes a capability matrix and the tools obey it. Resource levelling is the standing example:
Microsoft Project's heuristic is unpublished, so an imitation would be a different answer wearing
the same name.

**An imported schedule is never silently rescheduled.** Its dates are the ones Microsoft Project
computed. This server's critical-path engine matches it on schedules built here and measurably does
not on real ones, so using ours is an explicit request that warns first.

**Errors say what to do instead.** A refusal with no next step is no better than a crash. If you add
a failure path, write the message for the person who will hit it at 6pm on a deadline.

**Tasks are addressed by `uid`, never by row `id`.** The row number shifts when a task is inserted,
and addressing by it edits the wrong task.

## Things worth knowing before you debug something

- **MPXJ is not thread-safe.** Every tool that touches a document runs inside that document's lock,
  which is reentrant because several tools are built on others.
- **A task added to a file read from disk gets no unique id from MPXJ.** It has to be assigned, or
  the task is invisible to every query.
- **`ProjectProperties.FinishDate` is derived from the task dates**, not stored. Using it as an
  anchor makes the scheduler fight its own previous answer.
- **Hours per working day come from the calendar**, not from an assumption. Real ones run 8, 9 and
  9.5, and the error compounds along every chain.

## Adding a tool

Keep the surface small. Twenty-five tools is already a lot of schema in every prompt, and the
alternative servers show what happens past that: 79 tools that nobody can hold in their head.
Prefer a new operation on an existing tool over a new tool.

Every tool needs a description that says what it does, what it costs, and when not to use it.

## Tests

A change that fixes a bug should come with the check that would have caught it. Several of the
suites' assertions exist because a real schedule broke something in a way no synthetic fixture ever
would have.

## Releasing

Publishing runs from a tag and authenticates with NuGet Trusted Publishing, so there is no API key
anywhere — not in a secret, not in a terminal, not in this repository. NuGet trusts a short-lived
token GitHub mints for this repository and `release.yml` specifically.

1. Set `<Version>` in `src/HorizunMsProjectMcp/HorizunMsProjectMcp.csproj`, in
   `.mcp/server.json` (both fields), in `packaging/claude-desktop/manifest.json`, in the
   `VERSION` constant of `packaging/claude-desktop/launcher.js`, and in the four client manifests
   under `.claude-plugin/`, `.codex-plugin/` and `.agents/plugins/`. `packaging-test.py` lists
   every one of them and fails until they agree.
2. Add the release to `CHANGELOG.md`.
3. `git tag v1.1.0 && git push origin v1.1.0`.

The workflow runs all six suites before it packs anything and refuses to publish if the built
version does not match the tag. A version on NuGet cannot be deleted afterwards, only hidden, which
is why nothing ships that has not passed everything first.

The trust policy lives at [nuget.org/account/trustedpublishing](https://www.nuget.org/account/trustedpublishing)
and is pinned to this repository and to this workflow file. Renaming `release.yml` breaks publishing
until the policy is updated — deliberately, since the file name is part of what NuGet is trusting.

### The MCP registry

Listing in the registry runs separately, in `registry.yml`, and waits for NuGet to finish indexing
before it starts. The registry proves ownership by fetching the published package and looking for
`mcp-name: io.github.HorizunGroup/horizun-msproject-mcp` in its README — which is why that line
lives in `README.md` and travels inside the package. Removing it silently breaks publishing to the
registry while leaving NuGet unaffected.

`.mcp/server.json` must declare the same version as the package it points at; the workflow refuses
otherwise, since a registry entry advertising a version nobody can install is worse than no entry.

The namespace is case-sensitive. The registry derives what you may publish from the GitHub owner
verbatim — `io.github.HorizunGroup/*` — and treats a lowercase claim as a different namespace
rather than the same one spelled casually.

### The Claude Desktop extension

`scripts/build_mcpb.py` packs `packaging/claude-desktop/` into a `.mcpb`, which `release.yml`
attaches to the GitHub release. The bundle carries a launcher, not the server: the server unpacks
to roughly 650 MB, and an extension that embedded it would be a download nobody wants and a copy
frozen at the version it shipped with. On first launch the launcher installs the package from NuGet
and runs it.

Two rules the build enforces, because neither failure is visible at runtime until a user hits it:
the bundle refuses to pack when any of those version declarations disagree, and the tool list it
advertises is read from a freshly built server rather than from a list somebody kept by hand.
