# ADR-0024: Shared Local State Architecture

**Status:** Accepted
**Date:** 2026-01-06
**Authors:** Josh, Claude

## Context

PPDS is a multi-interface platform with multiple UIs accessing the same user data:

1. **CLI Commands** (`ppds auth list`) - Traditional command-line
2. **TUI Application** (`ppds -i`) - Terminal.Gui interactive mode
3. **VS Code Extension** - Connects via `ppds serve` JSON-RPC daemon
4. **Future UIs** - Web, desktop app, etc.

Without explicit guidance, each UI might implement its own storage for auth profiles, query history, settings, etc. This leads to:

- **Data silos**: Login from TUI not available in CLI
- **Code duplication**: Each UI implementing file I/O
- **Inconsistent behavior**: Different caching, locking, validation
- **Maintenance burden**: Same bugs fixed in multiple places

## Decision

### Single Location

All user data lives in `~/.ppds/` (or `%LOCALAPPDATA%\PPDS` on Windows):

```
~/.ppds/
├── profiles.json           # Auth profiles
├── history/                # Query history per-environment
│   ├── {env-hash-1}.json
│   └── {env-hash-2}.json
├── settings.json           # User preferences
├── msal_token_cache.bin    # MSAL token cache
└── ppds.credentials.dat    # Encrypted credentials (DPAPI)
```

### Single Code Path

All access goes through Application Services (ADR-0015). UIs never read/write files directly.

```
CLI:        ppds auth list    → IProfileService.GetProfilesAsync()
TUI:        Profile Selector  → IProfileService.GetProfilesAsync()
VS Code:    RPC call          → ppds serve → IProfileService.GetProfilesAsync()
```

### Stateless UIs

UIs are "dumb views" that:
- Call Application Services for all persistent state
- Never manage file I/O, caching, or locking
- Format service responses for their display medium

### RPC Exposure

`ppds serve` exposes Application Services to remote clients:
- VS Code extension calls RPC methods
- RPC handlers delegate to the same services CLI/TUI use
- No special "remote" code path - same services, different transport

## Consequences

### Positive

- **Login from ANY interface = available in ALL interfaces**
- **No code duplication** - file I/O written once in services
- **Consistent behavior** - same caching, locking, validation
- **Testable** - services can be unit tested without UI

### Negative

- **Requires discipline** - UIs must resist temptation to read files directly
- **Service overhead** - simple reads go through abstraction layer

### Neutral

- **Existing pattern** - `ProfileStore` already follows this model
- **No new package** - services stay in PPDS.Cli

## Implementation Guidelines

### Services Own File Access

```csharp
// CORRECT: Service handles file I/O
public class QueryHistoryService : IQueryHistoryService
{
    private readonly string _historyDir = ProfilePaths.GetHistoryDirectory();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string environmentUrl)
    {
        await _lock.WaitAsync();
        try
        {
            var path = GetHistoryPath(environmentUrl);
            if (!File.Exists(path)) return Array.Empty<HistoryEntry>();
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<HistoryFile>(json)?.Queries ?? [];
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

### UIs Call Services

```csharp
// CORRECT: TUI calls service
var history = await _queryHistoryService.GetHistoryAsync(environmentUrl);
var listView = new ListView { Source = history.Select(h => h.Sql).ToList() };

// WRONG: TUI reads file directly
var json = File.ReadAllText("~/.ppds/history/abc123.json"); // NO!
```

### New Data = New Service

When adding new persistent data:
1. Add file to `~/.ppds/` directory structure
2. Create `I{Name}Service` interface
3. Create `{Name}Service` implementation with file I/O
4. Register in `AddCliApplicationServices()`
5. Expose via RPC in `ppds serve`

## References

- ADR-0015: Application Service Layer for CLI/TUI/Daemon
- ADR-0007: Unified CLI and Auth Profiles
- Existing pattern: `ProfileStore.cs`, `SecureCredentialStore.cs`
