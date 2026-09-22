#!/usr/bin/env python3
"""Packaging checks for horizun-msproject-mcp.

Nine files now repeat the version, the package id and the command name. Nothing
in a build catches them drifting apart: the server still starts, the tests still
pass, and the only symptom is a client that installs one thing and runs another.
So this suite reads every one of them and insists they agree.

    python tools/packaging-test.py

Exits non-zero on the first failure.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

PACKAGE_ID = "HorizunMsProjectMcp"
COMMAND = "horizun-msproject-mcp"
REGISTRY_NAME = "io.github.HorizunGroup/horizun-msproject-mcp"

passed = 0
failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    global passed
    if condition:
        passed += 1
        print(f"  ok    {name}")
    else:
        failures.append(f"{name}{': ' + detail if detail else ''}")
        print(f"  FAIL  {name}{': ' + detail if detail else ''}")


def read_json(relative: str) -> dict:
    return json.loads((ROOT / relative).read_text(encoding="utf-8"))


def read_text(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


print("versions agree")
csproj = read_text("src/HorizunMsProjectMcp/HorizunMsProjectMcp.csproj")
version = re.search(r"<Version>([^<]+)</Version>", csproj).group(1)

registry = read_json(".mcp/server.json")
mcpb = read_json("packaging/claude-desktop/manifest.json")
claude_plugin = read_json(".claude-plugin/plugin.json")
codex_plugin = read_json(".codex-plugin/plugin.json")
claude_market = read_json(".claude-plugin/marketplace.json")
agents_market = read_json(".agents/plugins/marketplace.json")
launcher = read_text("packaging/claude-desktop/launcher.js")

check("csproj version is semver", bool(re.fullmatch(r"\d+\.\d+\.\d+", version)), version)
check(".mcp/server.json server version", registry["version"] == version, registry["version"])
check(".mcp/server.json package version", registry["packages"][0]["version"] == version)
check("mcpb manifest version", mcpb["version"] == version, mcpb["version"])
check("launcher VERSION constant",
      re.search(r"^const VERSION = '([^']+)';", launcher, re.M).group(1) == version)
check(".claude-plugin version", claude_plugin["version"] == version)
check(".codex-plugin version", codex_plugin["version"] == version)
check("claude marketplace ref tag",
      claude_market["plugins"][0]["source"]["ref"] == f"v{version}")
check("agents marketplace ref tag",
      agents_market["plugins"][0]["source"]["ref"] == f"v{version}")

print("\nidentifiers agree")
check("registry name casing matches the org", registry["name"] == REGISTRY_NAME, registry["name"])
check("registry package identifier", registry["packages"][0]["identifier"] == PACKAGE_ID)
check("registry base url is NuGet's service index",
      registry["packages"][0]["registryBaseUrl"] == "https://api.nuget.org/v3/index.json")
check("csproj PackageId", f"<PackageId>{PACKAGE_ID}</PackageId>" in csproj)
check("csproj ToolCommandName", f"<ToolCommandName>{COMMAND}</ToolCommandName>" in csproj)
check("launcher installs the published package id", f"const PACKAGE = '{PACKAGE_ID}'" in launcher)
check("launcher runs the published command", f"const COMMAND = '{COMMAND}'" in launcher)

# The registry proves ownership by finding this marker in the README *inside*
# the published package, so a mismatch here is a failed publish, not a typo.
readme = read_text("README.md")
check("README carries the registry ownership marker",
      f"<!-- mcp-name: {REGISTRY_NAME} -->" in readme)
check("README is packed into the nupkg", 'Include="../../README.md" Pack="true"' in csproj)
check("NOTICE is packed into the nupkg", 'Include="../../NOTICE" Pack="true"' in csproj)

print("\nclients point at the same command")
for label, doc in (("claude-plugin", claude_plugin), ("codex-plugin", codex_plugin)):
    server = doc["mcpServers"][COMMAND]
    check(f"{label} command", server["command"] == COMMAND, server["command"])
example = read_json(".mcp.json.example")
check(".mcp.json.example command", example["mcpServers"][COMMAND]["command"] == COMMAND)
check(".mcp.json.example is stdio", example["mcpServers"][COMMAND]["type"] == "stdio")

print("\nthe Claude Desktop bundle is sound")
check("mcpb entry point exists", (ROOT / "packaging/claude-desktop/launcher.js").exists())
check("mcpb runs the launcher it ships",
      mcpb["server"]["args" if "args" in mcpb["server"] else "mcp_config"]["args"]
      == ["${__dirname}/launcher.js"])
check("mcpb declares the platforms the server runs on",
      sorted(mcpb["compatibility"]["platforms"]) == ["darwin", "linux", "win32"])
node = subprocess.run([("node.exe" if sys.platform == "win32" else "node"), "--check",
                       str(ROOT / "packaging/claude-desktop/launcher.js")],
                      capture_output=True, text=True)
check("launcher.js parses", node.returncode == 0, node.stderr.strip()[:120])

print("\ndocumentation covers every client")
install = read_text("docs/INSTALL.md")
for client in ("Claude Desktop", "Claude Code", "Codex", "ChatGPT"):
    check(f"INSTALL.md covers {client}", f"## {client}" in install)
check("INSTALL.md is honest that ChatGPT needs a bridge",
      "cannot reach it directly" in install)
check("README links the install guide", "docs/INSTALL.md" in readme)

print()
total = passed + len(failures)
if failures:
    print(f"{len(failures)} of {total} checks failed:")
    for f in failures:
        print(f"  - {f}")
    sys.exit(1)
print(f"{passed}/{total} packaging checks passed")
