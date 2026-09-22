# Security policy

## Reporting a vulnerability

Report privately through
[GitHub's advisory form](https://github.com/HorizunGroup/horizun-msproject-mcp/security/advisories/new).
Please do not open a public issue for a vulnerability.

Expect an acknowledgement within three working days, and an assessment within
ten. If a fix is warranted it ships in a patch release, and the advisory credits
the reporter unless they ask otherwise.

## Supported versions

The latest minor release receives fixes. A version on NuGet cannot be deleted,
only hidden, so an affected version is superseded rather than withdrawn.

## What this server can reach

Worth knowing before you connect it:

- It **reads and writes project files** anywhere the account running it can
  reach. Paths come from the client, so an agent asked to "open the schedule"
  can open any file the user could.
- It **makes no network calls.** Nothing is uploaded, and there is no telemetry.
  Schedules stay on the machine.
- On Windows, native `.mpp` writing **drives Microsoft Project through COM**,
  which starts a real Project process under the same account.
- It runs over **stdio**, as a child process of the client. It opens no port and
  listens on nothing.

That last point is the one people undo. Putting an HTTP bridge in front of it to
reach it from ChatGPT (see [docs/INSTALL.md](docs/INSTALL.md#chatgpt)) turns a
local tool into a reachable service that reads and writes files. Authenticate
the tunnel; a public no-auth endpoint is not an acceptable shortcut.

## Supply chain

Releases are published to NuGet with
[Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):
the workflow exchanges a short-lived GitHub OIDC token for a key that lives for
minutes inside the runner. There is no long-lived API key in a secret, in a
terminal, or anywhere else — nothing to leak and nothing to rotate. The MCP
registry entry is published the same way.
