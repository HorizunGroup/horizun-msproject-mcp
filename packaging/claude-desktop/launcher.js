#!/usr/bin/env node
'use strict';

/*
 * Claude Desktop extension launcher.
 *
 * The extension does NOT carry the server. HorizunMsProjectMcp unpacks to about
 * 650 MB once MPXJ's IKVM assemblies are on disk, and shipping that inside a
 * .mcpb would make a download nobody wants and a copy that goes stale the day
 * the next version ships. So this resolves the copy that `dotnet tool` already
 * installed, and installs it on first launch when it is missing.
 *
 * stdout is the JSON-RPC channel. A single stray byte written to it desynchronises
 * the framing and the client drops the session, so every diagnostic in this file
 * goes to stderr, and the installer's own chatter is redirected there too.
 */

const { spawn, spawnSync } = require('node:child_process');
const { existsSync } = require('node:fs');
const { join, delimiter } = require('node:path');
const { homedir, platform } = require('node:os');

const PACKAGE = 'HorizunMsProjectMcp';
const COMMAND = 'horizun-msproject-mcp';
// Kept equal to the manifest's version by scripts/build_mcpb.py, which refuses
// to pack when the two disagree. An extension that installs a different server
// than it advertises is worse than one that fails to build.
const VERSION = '1.2.1';

const EXE = platform() === 'win32' ? `${COMMAND}.exe` : COMMAND;

function say(message) {
  process.stderr.write(`[${COMMAND}] ${message}\n`);
}

function fail(lines) {
  for (const line of lines) say(line);
  process.exit(1);
}

/** Where `dotnet tool install --global` puts its shims, and anywhere else worth a look. */
function candidatePaths() {
  const found = [];
  // DOTNET_CLI_HOME moves the whole tools directory; honour it before guessing.
  for (const root of [process.env.DOTNET_CLI_HOME, homedir()].filter(Boolean)) {
    found.push(join(root, '.dotnet', 'tools', EXE));
  }
  // A tool installed to a custom --tool-path is only discoverable through PATH.
  for (const dir of (process.env.PATH || '').split(delimiter)) {
    if (dir) found.push(join(dir, EXE));
  }
  return found;
}

function locate() {
  // An explicit override is an instruction, not a hint: if someone pointed this
  // at a specific executable, quietly running a different one would be worse
  // than stopping, so a missing override is fatal rather than skipped.
  const override = process.env.HORIZUN_MSPROJECT_MCP_COMMAND;
  if (override) {
    if (existsSync(override)) return override;
    fail([
      'HORIZUN_MSPROJECT_MCP_COMMAND points at something that is not there:',
      `  ${override}`,
      '',
      'Correct it, or unset it to let this extension find the installed server.',
    ]);
  }
  return candidatePaths().find((p) => existsSync(p));
}

function haveDotnet() {
  const probe = spawnSync('dotnet', ['--version'], { stdio: 'ignore', shell: false });
  return !probe.error && probe.status === 0;
}

function install() {
  say(`${PACKAGE} ${VERSION} is not installed yet. Installing it now — this is a`);
  say('large package (about 140 MB) and the first run can take a few minutes.');
  // The installer's stdout is redirected to our stderr (fd 2) so its progress
  // output cannot reach the protocol channel.
  const run = spawnSync(
    'dotnet',
    ['tool', 'install', '--global', PACKAGE, '--version', VERSION],
    { stdio: ['ignore', 2, 2], shell: false },
  );
  return !run.error && run.status === 0;
}

let server = locate();

if (!server) {
  if (!haveDotnet()) {
    fail([
      'Could not find the server, and the .NET SDK is not on PATH either.',
      '',
      'Install the .NET 8 SDK from https://dotnet.microsoft.com/download, then',
      'either restart Claude Desktop to let this extension install the server,',
      `or install it yourself with:  dotnet tool install -g ${PACKAGE}`,
    ]);
  }
  if (!install()) {
    fail([
      `Installing ${PACKAGE} ${VERSION} failed.`,
      '',
      'Run this in a terminal to see the full reason:',
      `  dotnet tool install -g ${PACKAGE} --version ${VERSION}`,
    ]);
  }
  server = locate();
  if (!server) {
    fail([
      'The installer reported success but the executable is still not where it',
      `was expected: ${join(homedir(), '.dotnet', 'tools', EXE)}`,
      '',
      'If you use a custom tool path, point this extension at it by setting',
      'HORIZUN_MSPROJECT_MCP_COMMAND to the full path of the executable.',
    ]);
  }
  say('Installed. Starting.');
}

// 'inherit' hands our stdin/stdout straight to the server, so the protocol runs
// between the client and the server with nothing of ours in the middle to buffer,
// re-encode or truncate it.
const child = spawn(server, process.argv.slice(2), { stdio: 'inherit', shell: false });

child.on('error', (error) => {
  say(`Could not start ${server}: ${error.message}`);
  process.exit(1);
});

// Let the server decide how the session ends; mirror its outcome so the client
// sees the real exit status rather than ours.
child.on('exit', (code, signal) => {
  if (signal) process.kill(process.pid, signal);
  else process.exit(code ?? 0);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => { if (!child.killed) child.kill(signal); });
}
