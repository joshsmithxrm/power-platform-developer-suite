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

## Available Tools

### Authentication & Environment

| Tool | Description |
|------|-------------|
| `ppds_auth_who` | Get the current authentication profile context including identity, connected environment, and token status. |
| `ppds_env_list` | List available Dataverse environments accessible with the current profile. Supports filtering by name, URL, or ID. |
| `ppds_env_select` | Select a Dataverse environment by URL, display name, or unique name. All subsequent tools use the selected environment. |

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
ppds auth create --method DeviceCode

# Service principal (for CI/CD and automation)
ppds auth create --method ClientSecret --client-id <id> --tenant-id <tid>
```

After creating a profile and selecting an environment, the MCP server automatically uses those credentials for all Dataverse operations.

## Target Frameworks

- `net8.0`
- `net9.0`
- `net10.0`

## License

MIT License
