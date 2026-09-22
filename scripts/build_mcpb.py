#!/usr/bin/env python3
"""Build the Claude Desktop extension (.mcpb).

The bundle is deliberately tiny: it carries a launcher, not the server. What it
must never carry is a claim it cannot back, so this script refuses to pack when
the version in the manifest, the launcher, the csproj and the registry manifest
disagree, and it fills the advertised tool list by asking a freshly built server
what it actually exposes rather than trusting a list somebody typed.

    python scripts/build_mcpb.py [--configuration Debug|Release]

Output: dist/horizun-msproject-<version>.mcpb
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKAGING = ROOT / "packaging" / "claude-desktop"
DIST = ROOT / "dist"
STAGE = DIST / "mcpb"
CSPROJ = ROOT / "src" / "HorizunMsProjectMcp" / "HorizunMsProjectMcp.csproj"
REGISTRY = ROOT / ".mcp" / "server.json"
EXE_NAME = "horizun-msproject-mcp.exe" if sys.platform == "win32" else "horizun-msproject-mcp"


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def declared_versions() -> dict[str, str]:
    """Every place this version is written down, so they can be compared."""
    manifest = json.loads((PACKAGING / "manifest.json").read_text(encoding="utf-8"))
    registry = json.loads(REGISTRY.read_text(encoding="utf-8"))
    csproj = CSPROJ.read_text(encoding="utf-8")
    launcher = (PACKAGING / "launcher.js").read_text(encoding="utf-8")

    csproj_version = re.search(r"<Version>([^<]+)</Version>", csproj)
    launcher_version = re.search(r"^const VERSION = '([^']+)';", launcher, re.M)
    if not csproj_version:
        fail("no <Version> in the csproj")
    if not launcher_version:
        fail("no VERSION constant in launcher.js")

    return {
        "packaging/claude-desktop/manifest.json": manifest["version"],
        "packaging/claude-desktop/launcher.js": launcher_version.group(1),
        "src/HorizunMsProjectMcp/HorizunMsProjectMcp.csproj": csproj_version.group(1),
        ".mcp/server.json (server)": registry["version"],
        ".mcp/server.json (package)": registry["packages"][0]["version"],
    }


def advertised_tools(exe: Path) -> list[dict[str, str]]:
    """Ask the server itself. A hand-kept list drifts; this one cannot."""
    proc = subprocess.Popen(
        [str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )
    try:
        def send(msg: dict) -> None:
            proc.stdin.write(json.dumps(msg) + "\n")
            proc.stdin.flush()

        def read() -> dict:
            while True:
                line = proc.stdout.readline()
                if not line:
                    fail("the server closed the connection while listing its tools")
                line = line.strip()
                if line.startswith("{"):
                    return json.loads(line)

        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "build_mcpb", "version": "1"}}})
        read()
        send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tools = read()["result"]["tools"]
    finally:
        proc.stdin.close()
        proc.terminate()

    # The manifest schema takes a name and a description only; inputSchema belongs
    # to the protocol, not to the extension's shop window.
    return [
        {"name": t["name"], "description": (t.get("description") or "").strip().split("\n")[0]}
        for t in sorted(tools, key=lambda t: t["name"])
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    args = parser.parse_args()

    versions = declared_versions()
    distinct = set(versions.values())
    if len(distinct) != 1:
        lines = "\n".join(f"  {v}  {where}" for where, v in versions.items())
        fail(f"the version is not the same everywhere:\n{lines}")
    version = distinct.pop()

    exe = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / args.configuration / "net8.0" / EXE_NAME
    if not exe.exists():
        fail(f"build the server first — {exe} does not exist\n"
             f"       dotnet build src/HorizunMsProjectMcp -c {args.configuration}")

    tools = advertised_tools(exe)
    if not tools:
        fail("the server advertised no tools")

    manifest = json.loads((PACKAGING / "manifest.json").read_text(encoding="utf-8"))
    manifest["tools"] = tools

    if STAGE.exists():
        shutil.rmtree(STAGE)
    STAGE.mkdir(parents=True)
    (STAGE / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    shutil.copy2(PACKAGING / "launcher.js", STAGE / "launcher.js")
    for extra in ("README.md", "LICENSE"):
        shutil.copy2(ROOT / extra, STAGE / extra)

    npx = shutil.which("npx") or shutil.which("npx.cmd")
    if not npx:
        fail("npx is required to pack the bundle (install Node.js)")

    cli = ["@anthropic-ai/mcpb@latest"]
    subprocess.run([npx, "--yes", *cli, "validate", str(STAGE / "manifest.json")], check=True)

    out = DIST / f"horizun-msproject-{version}.mcpb"
    out.unlink(missing_ok=True)
    subprocess.run([npx, "--yes", *cli, "pack", str(STAGE), str(out)], check=True)

    size_kb = out.stat().st_size / 1024
    print(f"\n{out.relative_to(ROOT)}  —  {size_kb:.0f} KB, {len(tools)} tools, version {version}")


if __name__ == "__main__":
    main()
