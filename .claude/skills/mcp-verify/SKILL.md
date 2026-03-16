---
name: mcp-verify
description: How to verify MCP tools — Inspector usage, direct invocation, response validation. Use when testing MCP changes or verifying tool behavior.
---

# MCP Verify

Supporting knowledge for `/verify mcp` and `/qa mcp`. Documents how to interact with and verify MCP server tools.

## Build

```bash
dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -f net10.0
```

## MCP Inspector

The MCP Inspector provides a web UI for testing tools interactively:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

This opens a web UI where you can:
- List available tools
- Invoke tools with parameters
- See JSON responses
- Test session options

## Direct Tool Invocation

For programmatic testing, invoke tools via the MCP protocol:

```bash
# List tools
echo '{"jsonrpc":"2.0","method":"tools/list","id":1}' | ppds-mcp-server

# Call a tool
echo '{"jsonrpc":"2.0","method":"tools/call","params":{"name":"ppds_query_sql","arguments":{"sql":"SELECT TOP 1 name FROM account"}},"id":2}' | ppds-mcp-server
```

## Session Options

Test session configuration flags:

| Flag | Purpose | Test |
|------|---------|------|
| `--profile <name>` | Lock to specific profile | Invoke with wrong profile, verify rejection |
| `--environment <url>` | Lock to specific environment | Verify tools use the locked environment |
| `--read-only` | Prevent DML operations | Run INSERT/UPDATE/DELETE, verify rejection |
| `--allowed-env <urls>` | Restrict environment switching | Try switching to disallowed env, verify rejection |

## Response Validation

For each tool invocation, verify:
1. Response is valid JSON
2. `content` array contains expected text/data
3. `isError` is false for success cases, true for error cases
4. Error messages are descriptive (not raw exceptions)

## Common Failure Modes

| Symptom | Likely Cause |
|---------|-------------|
| "No active profile" | Session not initialized, or profile flag wrong |
| Timeout | Dataverse connection issue, check environment URL |
| Empty results | Query is valid but no matching data |
| "Operation not permitted" | `--read-only` flag blocking DML |
