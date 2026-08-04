#!/usr/bin/env python3
"""Smoke test for horizun-msproject-mcp: speaks real JSON-RPC over stdio.

Verifies the server initializes, advertises its tools, and that project_health
returns a usable report. Exits non-zero on the first failure.

    python tools/smoke-test.py [--deep]

--deep makes project_health actually launch Microsoft Project instead of only
reading its COM registration.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "src" / "HorizunMsProjectMcp" / "bin" / "Debug" / "net8.0" / "horizun-msproject-mcp.exe"


class Client:
    """Minimal MCP stdio client. Keeps stdin open so the server can flush replies."""

    def __init__(self, exe: Path) -> None:
        self.proc = subprocess.Popen(
            [str(exe)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self._id = 0

    def request(self, method: str, params: dict | None = None) -> dict:
        self._id += 1
        self._send({"jsonrpc": "2.0", "id": self._id, "method": method, "params": params or {}})
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError(f"server closed the stream while awaiting '{method}'")
            msg = json.loads(line)
            if msg.get("id") == self._id:
                if "error" in msg:
                    raise RuntimeError(f"{method} failed: {msg['error']}")
                return msg["result"]

    def notify(self, method: str, params: dict | None = None) -> None:
        self._send({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def _send(self, payload: dict) -> None:
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()

    def close(self) -> None:
        self.proc.stdin.close()
        self.proc.wait(timeout=10)


def main() -> int:
    deep = "--deep" in sys.argv

    if not EXE.exists():
        print(f"FAIL  build first: {EXE} not found", file=sys.stderr)
        return 1

    client = Client(EXE)
    checks = 0
    failures = 0

    def check(label: str, ok: bool, detail: str = "") -> None:
        nonlocal checks, failures
        checks += 1
        if not ok:
            failures += 1
        print(f"  [{'PASS' if ok else 'FAIL'}] {label}{f': {detail}' if detail else ''}")

    try:
        init = client.request(
            "initialize",
            {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "smoke-test", "version": "1.0"},
            },
        )
        client.notify("notifications/initialized")

        print("handshake:")
        check("server responds to initialize", "serverInfo" in init)
        check("declares tools capability", "tools" in init.get("capabilities", {}))

        print("tools:")
        tools = {t["name"]: t for t in client.request("tools/list")["tools"]}
        for name in sorted(tools):
            print(f"  - {name}")
        check("project_health is advertised", "project_health" in tools)
        check(
            "project_health documents its deep flag",
            "deep" in tools.get("project_health", {}).get("inputSchema", {}).get("properties", {}),
        )

        print(f"project_health(deep={str(deep).lower()}):")
        result = client.request(
            "tools/call", {"name": "project_health", "arguments": {"deep": deep}}
        )
        check("call did not error", not result.get("isError", False))

        report = json.loads(result["content"][0]["text"])
        print(json.dumps(report, indent=2))

        check("names a backend", report.get("backend") in {"mpxj", "com"})
        check("reports the runtime", bool(report.get("runtime", {}).get("framework")))

        caps = report.get("capabilities", {})
        check("publishes a capability matrix", bool(caps))
        check("reading a schedule is always available", caps.get("read_schedule") is True)

        # Scheduling, recalculation and dry-run simulation are served by this server's own
        # critical-path engine, so they hold on the file backend too.
        for capability in ("recalculate", "dry_run_simulation", "reschedule_incomplete"):
            check(f"{capability} is available on any backend", caps.get(capability) is True)

        # Resource levelling is the one thing that is never approximated: Microsoft Project's
        # heuristic is unpublished, so an imitation would be a different answer under the same name.
        if report.get("backend") == "mpxj":
            check(
                "resource levelling is not claimed on the file backend",
                caps.get("level_resources") is False,
                f"level_resources={caps.get('level_resources')}",
            )

        project = report.get("microsoftProject", {})
        if not project.get("available"):
            check(
                "a failed COM probe explains itself",
                bool(project.get("diagnosis")),
                "no diagnosis attached",
            )
            check(
                "a failed COM probe offers a repair path",
                bool(project.get("repair")),
                "no repair steps attached",
            )
    finally:
        client.close()

    print(f"\n=== {checks - failures}/{checks} checks passed ===")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
