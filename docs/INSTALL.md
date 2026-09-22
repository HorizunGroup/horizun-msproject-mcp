# Install

The server is one .NET global tool. Every client below runs the same executable;
none of them needs Java, a Microsoft Project licence, or a paid driver.

```bash
dotnet tool install -g HorizunMsProjectMcp
```

That needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). It puts
`horizun-msproject-mcp` on your PATH, in `~/.dotnet/tools`. To upgrade later,
`dotnet tool update -g HorizunMsProjectMcp`.

Windows, macOS and Linux all work. Two things are Windows-only, because they are
Windows-only in the world: writing a native `.mpp` (which drives a licensed
Microsoft Project through COM) and the COM half of `project_health`. Reading
`.mpp` needs neither and works everywhere.

## Which client needs what

| Client | How it connects | Needs the tool installed first |
|---|---|---|
| **Claude Desktop** | `.mcpb` extension from [Releases](https://github.com/HorizunGroup/horizun-msproject-mcp/releases) | No — the extension installs it on first launch |
| **Claude Code** | plugin, or one `claude mcp add` | Yes |
| **Codex** | plugin, or four lines in `config.toml` | Yes |
| **ChatGPT** | needs an HTTPS bridge — see below | Yes |

---

## Claude Desktop

Claude Desktop installs MCP servers as `.mcpb` extension bundles, so this is the
one client that does not need you to run anything first.

1. Download `horizun-msproject-<version>.mcpb` from the
   [latest release](https://github.com/HorizunGroup/horizun-msproject-mcp/releases/latest).
2. Open **Settings → Extensions**.
3. Drag the `.mcpb` onto the page, or use **Advanced settings → Install extension**.
4. Review what it asks for, enable it, and restart Claude Desktop.

The bundle is about 15 KB because it does not carry the server: the server
unpacks to roughly 650 MB once MPXJ's assemblies are on disk, which is not
something to ship inside an extension. On first launch the extension installs
`HorizunMsProjectMcp` from NuGet and runs that. This takes a few minutes the
first time and needs the .NET 8 SDK present; if it is missing, the extension
says so in its error rather than failing silently.

Already have the tool installed? The extension finds it and skips the download.

## Claude Code

As a plugin, which is the version-pinned route:

```bash
claude plugin marketplace add HorizunGroup/horizun-msproject-mcp
claude plugin install horizun-msproject-mcp@horizun
```

Or register the server directly:

```bash
claude mcp add horizun-msproject-mcp -- horizun-msproject-mcp
```

Add `-s user` to that command to make it available in every project rather than
only the current one. For project scope committed to a repo, copy
[`.mcp.json.example`](../.mcp.json.example) to `.mcp.json`.

## Codex

```bash
codex mcp add horizun-msproject-mcp -- horizun-msproject-mcp
```

Or write it into `~/.codex/config.toml` yourself:

```toml
[mcp_servers.horizun_msproject_mcp]
command = "horizun-msproject-mcp"
args = []
```

Restart Codex afterwards.

## ChatGPT

**This one is not a drop-in, and it is worth being clear about why.** ChatGPT
connectors talk to MCP servers over HTTPS. This server speaks stdio, like every
local MCP server — it is a process on your machine, not a website. So ChatGPT
cannot reach it directly, and no configuration file changes that.

To use it from ChatGPT you put a bridge in front: a stdio-to-HTTP proxy such as
[`mcp-proxy`](https://github.com/sparfenyuk/mcp-proxy) or
[`supergateway`](https://github.com/supercorp-ai/supergateway), exposed through a
tunnel (Cloudflare Tunnel, ngrok) so ChatGPT can reach it. Then add that URL
under **Settings → Connectors** in developer mode.

```bash
npx -y supergateway --stdio "horizun-msproject-mcp" --port 8000
```

Think before you do this on a machine that holds client schedules. That bridge
publishes a tool surface that reads and writes project files to whoever can
reach the URL, so the tunnel needs authentication, and a public no-auth endpoint
is not an acceptable shortcut. Claude Desktop, Claude Code and Codex all run the
server locally and need none of this.

## Verify it

Ask the client to call `project_health`. It reports the platform, whether
Microsoft Project is installed, and — on Windows — whether its COM server
actually starts, with the repair steps when it does not. It is a diagnosis, not
a ping: if something is wrong, its answer says what.

## Uninstall

```bash
dotnet tool uninstall -g HorizunMsProjectMcp
```

Remove the extension in Claude Desktop's **Settings → Extensions**, or the entry
from the client's configuration, as well.
