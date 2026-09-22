# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

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

[1.1.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.1.0
[1.0.2]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/HorizunGroup/horizun-msproject-mcp/releases/tag/v1.0.0
