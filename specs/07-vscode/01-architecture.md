# VS Code Extension: Architecture

## Overview

The Power Platform Developer Suite VS Code Extension is a thin TypeScript UI layer that communicates with the PPDS CLI daemon via JSON-RPC over stdio. Following ADR-0027 (Multi-Interface Development), the extension ports TUI patterns to VS Code while delegating all business logic to Application Services accessed through the `ppds serve` daemon.

## Public API

### Commands

| Command | Title | Description |
|---------|-------|-------------|
| `ppds.listProfiles` | PPDS: List Profiles | Shows Quick Pick with authentication profiles |

### Extension Manifest

| Field | Value |
|-------|-------|
| `name` | power-platform-developer-suite |
| `displayName` | Power Platform Developer Suite |
| `version` | 0.4.0-alpha.1 |
| `publisher` | JoshSmithXRM |
| `preview` | true |
| `engines.vscode` | ^1.85.0 |

### Activation Events

```json
"activationEvents": ["onCommand:ppds.listProfiles"]
```

Extension activates lazily on first command invocation.

## Behaviors

### Architecture Pattern

```
VS Code Extension (TypeScript)
        │
        ▼
   JSON-RPC over stdio
        │
        ▼
   ppds serve (CLI daemon)
        │
        ▼
   Application Services
        │
        ▼
   Dataverse Connection Pool
```

### Extension Lifecycle

**Activation:**
1. User invokes registered command
2. `activate(context)` called by VS Code
3. `DaemonClient` singleton created
4. Commands registered to subscriptions
5. Disposables tracked for cleanup

**Deactivation:**
1. `deactivate()` called on extension unload
2. Disposables cleaned up automatically
3. Daemon process terminated

### DaemonClient Lifecycle

```
User invokes command
        │
        ▼
   DaemonClient.ensureConnected()
        │
        ├── Not connected: spawn `ppds serve`
        │         │
        │         ├── Pipe stdin/stdout for JSON-RPC
        │         ├── Capture stderr to OutputChannel
        │         └── Call connection.listen()
        │
        └── Connected: use existing connection
        │
        ▼
   Send RPC request
        │
        ▼
   Receive response / handle error
```

### Command Flow (ppds.listProfiles)

```
1. User: Run "PPDS: List Profiles" command
2. Extension: await daemonClient.listProfiles()
3. DaemonClient: ensureConnected() → spawn daemon if needed
4. DaemonClient: send auth/list RPC request
5. Daemon: Load ProfileStore, build AuthListResponse
6. DaemonClient: receive response
7. Extension: Map profiles to Quick Pick items
8. Extension: Show Quick Pick UI
9. User: Select profile
10. Extension: Show info notification with selection
```

## RPC Protocol

### Communication

| Aspect | Value |
|--------|-------|
| Transport | stdio (stdin/stdout pipes) |
| Protocol | JSON-RPC 2.0 |
| Library | vscode-jsonrpc |
| Daemon | `ppds serve` command |

### Available RPC Methods

| Category | Method | Purpose |
|----------|--------|---------|
| Auth | `auth/list` | List all profiles |
| Auth | `auth/who` | Get active profile details |
| Auth | `auth/select` | Switch active profile |
| Environment | `env/list` | List discoverable environments |
| Environment | `env/select` | Bind environment to profile |
| Plugins | `plugins/list` | List registered plugins |
| Query | `query/fetch` | Execute FetchXML queries |
| Query | `query/sql` | Execute SQL queries |
| Solutions | `solutions/list` | List solutions |
| Solutions | `solutions/components` | Get solution components |
| Profile | `profiles/invalidate` | Invalidate connection pools |

### RPC Notifications (Server to Client)

| Notification | Purpose |
|--------------|---------|
| `auth/deviceCode` | Device code flow (user code, URL) |

### Response Types

```typescript
interface AuthListResponse {
    activeProfile: string | null;
    activeProfileIndex: number | null;
    profiles: ProfileInfo[];
}

interface ProfileInfo {
    index: number;
    name: string | null;
    identity: string;
    authMethod: string;
    cloud: string;
    environment: EnvironmentSummary | null;
    isActive: boolean;
    createdAt: string | null;
    lastUsedAt: string | null;
}
```

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Daemon not running | Spawned on first RPC call | Lazy initialization |
| Daemon exits | Connection cleaned up | Next call spawns new daemon |
| No profiles configured | Empty profile list | User needs `ppds auth create` |
| RPC timeout | Error shown to user | Retry is manual |
| stderr output | Sent to OutputChannel | Visible in Output panel |

## Error Handling

### RPC Exception Model

```csharp
class RpcException : LocalRpcException
{
    string StructuredErrorCode { get; }
    RpcErrorData ErrorData { get; set; }
}

class RpcErrorData
{
    string Code { get; set; }      // e.g., "Auth.ProfileNotFound"
    string Message { get; set; }   // Human-readable
    string? Details { get; set; }  // Stack trace (debug)
    string? Target { get; set; }   // Target entity/field
}
```

### Error Codes

| Code | Condition |
|------|-----------|
| `Auth.NoActiveProfile` | No profile configured |
| `Auth.ProfileNotFound` | Profile not found |
| `Validation.RequiredField` | Missing required parameter |
| `Validation.InvalidArguments` | Invalid parameter combination |
| `Connection.EnvironmentNotFound` | Environment not selected |
| `Query.ParseError` | SQL parse error |
| `Operation.NotSupported` | Feature not implemented |

### Extension Error Handling

```typescript
try {
    const result = await daemonClient!.listProfiles();
    // ... show UI
} catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    vscode.window.showErrorMessage(`Failed to list profiles: ${message}`);
}
```

## Dependencies

- **Internal:**
  - `ppds serve` - CLI daemon (RPC server)
  - Application Services (via daemon)
- **External:**
  - `vscode-jsonrpc@^8.2.0` - JSON-RPC implementation
  - `@types/vscode@1.85.0` - VS Code API types

## Configuration

### Extension Settings

None currently defined. Extension is minimal MVP.

### Build Configuration

| Tool | Version | Purpose |
|------|---------|---------|
| TypeScript | 5.0+ | Language |
| ESLint | 9.0+ | Linting |
| vsce | latest | Packaging |

### Build Scripts

| Script | Purpose |
|--------|---------|
| `npm run compile` | One-time compilation |
| `npm run watch` | Continuous build |
| `npm run lint` | ESLint validation |
| `npm run package` | Create .vsix |

## Thread Safety

- Extension runs in VS Code's extension host (single-threaded)
- DaemonClient maintains single connection
- Daemon handles concurrent RPC calls internally
- No explicit synchronization needed in extension

## Architecture Decisions

### Thin UI Layer

Extension offloads all business logic to CLI daemon:
- No direct Dataverse calls from extension
- All operations via RPC
- Extension only handles UI presentation

### Lazy Daemon Start

Daemon spawned on first RPC call, not on extension activation:
- Faster extension startup
- Resources not consumed until needed
- Daemon persists across multiple calls

### TUI-First Pattern

Following ADR-0027, feature development order:
1. Application Service (business logic)
2. CLI Command
3. TUI Panel (reference implementation)
4. RPC Method (daemon interface)
5. Extension View (ports TUI patterns)

Extension is last in order - benefits from stable patterns.

## Related

- [PPDS.Cli Services: Application Services](../04-cli-services/01-application-services.md) - Business logic
- [PPDS.TUI: Architecture](../05-tui/01-architecture.md) - Reference UI patterns
- ADR-0027: Multi-Interface Development

## Source Files

| File | Purpose |
|------|---------|
| `extension/src/extension.ts` | Extension activation & commands |
| `extension/src/daemonClient.ts` | JSON-RPC client for daemon |
| `extension/package.json` | Extension manifest |
| `src/PPDS.Cli/Commands/Serve/ServeCommand.cs` | Daemon startup |
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | RPC method implementations |
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcException.cs` | Error handling |
| `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs` | Daemon pool management |
