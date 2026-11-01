# PPDS.Mcp

MCP server exposing Power Platform capabilities for AI assistant integration.

## Installation

```bash
dotnet tool install -g PPDS.Mcp
```

## Quick Start - Claude Code

Add the following to your Claude Code MCP settings (`.claude/settings.json` or global settings):

```json
{
  "mcpServers": {
    "ppds": {
      "command": "ppds-mcp-server",
      "args": []
    }
  }
}
```

Once configured, Claude Code can query your Dataverse environment using natural language. The MCP server reads authentication from your PPDS profile (see [Authentication](#authentication) below).

## Quick Start - Other MCP Clients

PPDS.Mcp uses stdio transport. Launch the server and communicate over stdin/stdout:

```bash
ppds-mcp-server
```

Any MCP-compatible client can connect by spawning the process and exchanging JSON-RPC messages over stdio.

The server captures the profile and environment at startup. Use `--profile <name>`
and `--environment <url>` to select them explicitly. Global profile changes do
not redirect an existing server. Switching is disabled unless a destination is
listed with `--allowed-env <url>`; permitted switches affect future calls in this
session only. Calls already running retain their captured target.

## Available Tools

### Authentication & Environment

| Tool | Description |
|------|-------------|
| `ppds_auth_who` | Get the current authentication profile context including identity, connected environment, and token status. |
| `ppds_env_list` | List available Dataverse environments accessible with the current profile. Supports filtering by name, URL, or ID. |
| `ppds_env_select` | Select an allowed environment for subsequent calls in this session without changing global defaults. |

### Querying

| Tool | Description |
|------|-------------|
| `ppds_query_sql` | Execute a SQL SELECT query against Dataverse. Supports JOINs, WHERE, ORDER BY, TOP, and aggregate functions. SQL is transpiled to FetchXML via the PPDS.Query engine. |
| `ppds_query_fetch` | Execute a raw FetchXML query against Dataverse. Use for advanced query features not available in SQL. |

### Data & Schema Exploration

| Tool | Description |
|------|-------------|
| `ppds_data_analyze` | Analyze entity data: record count, primary attributes, and sample records. |
| `ppds_data_schema` | Get the schema (fields/attributes) for a Dataverse entity including types, constraints, and option set values. |
| `ppds_metadata_entity` | Get detailed entity metadata including attributes, relationships, keys, and ownership type. |

### Forms

| Tool | Description |
|------|-------------|
| `ppds_forms_list` | List a table's forms with stable IDs, types and managed status. |
| `ppds_forms_get` | Inspect published or unpublished form XML/layout; `unpublished=true` returns the draft fingerprint. |
| `ppds_forms_add_field` | Add one existing column to one existing Main-form section by IDs. Preview with `dryRun=true`; execution saves and verifies an unpublished draft. |
| `ppds_forms_publish` | Publish pending customizations for the selected table and verify the selected form. |

Read the unpublished form first, then supply its `draftHash` as
`expectedDraftHash`. A stale fingerprint fails before writing. Where Dataverse
supports row-version concurrency it is enforced; otherwise the result reports
`preflight-only` protection, so avoid simultaneous editors. Managed forms can
accept an unmanaged-layer edit only when their managed properties allow it.

Protected form writes require `confirmEnvironment` equal to the exact resolved
URL. Acknowledgements remain in memory for that session, identity, target and
policy. `--allowed-env` is independent of write acknowledgement, and `--read-only`
rejects mutations. Draft saves never publish. Publication additionally requires
`confirmPublishEntity` equal to the table logical name because it publishes other
pending customizations for that table too. Inspect phase results after a partial
failure; an error does not prove that an earlier write was undone.

### Plugin Analysis

| Tool | Description |
|------|-------------|
| `ppds_plugins_list` | List registered plugin assemblies with their types and step registrations. Supports filtering by assembly name. |
| `ppds_plugin_traces_list` | List plugin trace logs with filtering by entity, message, type name, or errors only. |
| `ppds_plugin_traces_get` | Get full details of a specific plugin trace including message block (trace output) and exception details. |
| `ppds_plugin_traces_timeline` | Build a hierarchical execution timeline for a transaction using correlation ID. Shows how plugins chain together and identifies performance bottlenecks. |

## Authentication

PPDS.Mcp reads authentication from your PPDS profile store. Create a profile before using the MCP server:

```bash
# Interactive browser login (recommended for development)
ppds auth create

# Device code flow (for remote/headless scenarios)
ppds auth create --deviceCode

# Service principal (for CI/CD and automation)
ppds auth create --name ci \
  --applicationId <id> \
  --clientSecret "$DATAVERSE_CLIENT_SECRET" \
  --tenant <tid> \
  --environment https://org.crm.dynamics.com
```

> **Note:** Avoid hardcoding the client secret in scripts or passing a literal value on the command line, where it can leak into shell history, CI logs, or process listings. Supply it from your CI/CD system's secret store via an environment variable, as shown above.

After creating a profile and selecting an environment, the MCP server automatically uses those credentials for all Dataverse operations.

## Target Frameworks

- `net8.0`
- `net9.0`
- `net10.0`

## License

MIT License

## Troubleshooting & Support

### Report a Bug

Found an issue? Please report it on GitHub:
[Report an Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)

Include:
- PPDS MCP server version: `ppds-mcp-server --version`
- Error message or unexpected behavior
- Steps to reproduce
- Your OS and .NET runtime version (`dotnet --info`)
