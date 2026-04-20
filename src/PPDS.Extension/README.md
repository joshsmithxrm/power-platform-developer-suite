# Power Platform Developer Suite

[![Version](https://img.shields.io/visual-studio-marketplace/v/JoshSmithXRM.power-platform-developer-suite)](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite)
[![Installs](https://img.shields.io/visual-studio-marketplace/i/JoshSmithXRM.power-platform-developer-suite)](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite)
[![License](https://img.shields.io/github/license/joshsmithxrm/power-platform-developer-suite)](https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/LICENSE)
[![Build](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml/badge.svg)](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml)

**Power Platform Developer Suite (PPDS)** is a developer platform for Microsoft Power Platform and Dataverse. This extension puts SQL/FetchXML notebooks, plugin registration, metadata browsing, and 7 other panels into VS Code — self-contained, with the `ppds` CLI daemon bundled — and an MCP server that makes your environments queryable by AI agents.

![Notebook mid-execution](media/notebook-hero.png)

## Zero-config install

Install from the Marketplace and you're done. The extension ships a platform-specific VSIX that bundles a self-contained, single-file `ppds` CLI daemon for your operating system. There is no separate download step, no global tool install, no .NET runtime to manage. The only thing you provide is a Dataverse authentication profile, and the sidebar walks you through creating one on first run. Stateless CI hosts can skip the profile entirely and authenticate through environment variables instead.

## Features

Nine connected panels, one notebook experience, and a shared authentication model that carries between them. Every panel talks to the bundled daemon over local JSON-RPC; no data leaves your machine unless you ship it out yourself.

### SQL and FetchXML Notebooks

Write queries in `.ppdsnb` notebooks with one cell per question. Toggle a cell between SQL and FetchXML with a single command; syntax highlighting, keyword completion, and column suggestions follow the switch. Results render inline with virtual scrolling so million-row selects stay responsive. Export any cell to CSV or JSON, or pop the query into the standalone Data Explorer for ad-hoc refinement. Query history persists per workspace and every execution records the environment it ran against.

### Metadata Browser

![Metadata Browser](media/metadata-browser.png)

Five-tab entity explorer with split-pane layout: entity list on the left, details on the right across Attributes, Relationships, Keys, Privileges, and Choices. Global option-set aggregation shows which entities share a choice list so you can refactor confidently. The environment picker supports search, filter, and pinned favorites.

### Plugin Traces

![Plugin Traces](media/plugin-traces.png)

Timeline waterfall over the plugin-trace log with trace-level management built in. A volume-warning banner flags when a run generated enough traces to trip the retention limit. The filter bar narrows by entity, message, or status, and the age-based cleanup action clears the backlog in one click. Deep links jump straight to the Maker Portal when you need to pivot.

### All nine panels

Uniform grid of every v1.0 panel — each opens directly on an environment from the Profiles tree:

- Data Explorer — fire-and-forget SQL/FetchXML over the active environment
- Solutions — browse, drill into component groups, open in the Maker Portal
- Plugin Registration — tree view over assemblies, steps, and images with enable/disable/unregister/download
- Connection References — status badges, related flow listing, orphan detection
- Environment Variables — type-aware editing with override and missing-value indicators
- Web Resources — solution filtering, binary-type protection, publish-selected action
- Import Jobs — search and operation-context column with filtered-status counts
- Metadata Browser — the five-tab explorer described above
- Plugin Traces — the timeline waterfall described above

### Profile and environment management

Sidebar tree groups every authentication profile and its discovered environments. Click to switch; the status-bar indicator shows the active profile and offers a quick-pick switcher. Environment color theming applies a 3-pixel top border by type — Dev, Test, Prod — so a stray production query is hard to miss.

## The `ppds` CLI daemon

The extension is a thin UI over `ppds serve`, the same daemon you can run headlessly. Every capability surfaced in a panel is also reachable from the CLI — profile management, environment discovery, solution export, plugin deployment, schema extraction, bulk migration, query execution. That means the same code paths cover local exploration, scripted automation, and CI jobs. When you want to promote a workflow out of the IDE, run `ppds` in a terminal or wire it into GitHub Actions; nothing about the extension locks you in. The daemon starts automatically on activation and stops when VS Code closes, so there is no background process to manage.

## AI-ready: MCP + scriptable CLI

PPDS ships a companion MCP server — `ppds-mcp-server` — that exposes more than twenty Dataverse tools to Claude Code, GitHub Copilot, and any MCP-speaking agent. Query entities, inspect metadata, analyze plugin registrations, and run FetchXML or SQL from the conversation. The CLI keeps a strict stdout/stderr contract (data on stdout, diagnostics on stderr) and supports JSON output on every read command, so coding agents that prefer to shell out get clean, parseable results instead of ANSI-wrapped prose. Install the MCP server once and point your AI assistant at it; the extension and the assistant talk to the same environments and share the same profiles.

## Quick Start

1. Install the extension from the Marketplace.
2. Open the PPDS sidebar via the activity-bar icon.
3. Create an authentication profile (or set the `PPDS_*` environment variables).
4. Select an environment from the Profiles tree.
5. Create a new notebook and run your first query.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `ppds.queryDefaultTop` | 100 | Default TOP value for SQL queries when none is specified. |
| `ppds.autoStartDaemon` | true | Start `ppds serve` automatically when the extension activates. |

## Part of PPDS — a developer platform

This extension is one surface of a broader platform. The whole thing is open source and every piece works without the others:

- **`ppds` CLI** — headless automation and CI/CD. Covers every `pac auth create` method, plus stateless env-var auth for CI/CD.
- **`ppds-mcp-server`** — 20+ Dataverse tools for AI assistants over the Model Context Protocol.
- **PPDS.\* NuGet libraries** — `PPDS.Dataverse`, `PPDS.Migration`, `PPDS.Query`, `PPDS.Plugins` — embed pooled connections, SQL→FetchXML transpilation, bulk data movement, and declarative plugin registration directly in your .NET apps.
- **`ppds-docs`** — platform documentation, architecture notes, and migration guides.

PPDS provides the pipeline building blocks — stateless env-var authentication, PAC-compatible deployment settings, and multi-profile bulk data — and complements `pac solution` for packaging rather than replacing it.

## Support

- [Report an issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)
- [Documentation](https://joshsmithxrm.github.io/ppds-docs/)
- Known limitations are tracked in the [CHANGELOG](CHANGELOG.md).

## License

MIT — see [LICENSE](LICENSE).
