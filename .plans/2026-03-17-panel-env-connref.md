# Phase 2a: Connection References + Environment Variables Panel Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Connection References and Environment Variables panels across all 4 surfaces (Daemon RPC, VS Code extension, TUI, MCP), plus a shared solution filter component reusable by future panels.

**Architecture:** Domain services (`IConnectionReferenceService`, `IConnectionService`, `IEnvironmentVariableService`) already exist. We add RPC endpoints in the daemon, VS Code webview panels with environment theming, TUI screens, and MCP tools. All surfaces call the same service methods (Constitution A1, A2).

**Tech Stack:** C# (.NET 8), TypeScript (VS Code extension + webview), Terminal.Gui (TUI), StreamJsonRpc (RPC), ModelContextProtocol (MCP)

**Spec:** `specs/panel-parity.md` — Panels 2 (Connection References) and 3 (Environment Variables), AC-CR-01 through AC-CR-10 and AC-EV-01 through AC-EV-10

**GitHub Issues:** #339, #340, #349, #350, #357, #359, #587, #588

---

## Design Decisions

### Shared solution filter component
Both panels need solution filter dropdowns. Rather than building inline and extracting later, we build the shared `SolutionFilter` component in `webview/shared/solution-filter.ts` alongside the existing `DataTable`. This component handles solution list loading, dropdown rendering, persisted selection (via `vscode.getState`), and change events. Phase 2d (Web Resources) will import it directly.

### SPN graceful degradation for Connections API
`IConnectionService` uses the Power Platform Admin API (`service.powerapps.com`), which requires user-delegated authentication. Service principals cannot access it. The RPC handler catches failures from `IConnectionService` and sets all connection statuses to `"N/A"`. The panel remains fully functional — it just shows "N/A" in the status column instead of "Connected"/"Error". A warning is logged but never surfaced to the user as an error.

### Environment Variables edit flow — modal dialog
The `environmentVariables/set` write operation is surfaced in VS Code via an edit button that opens a modal dialog. The dialog shows the variable type, current value, default value, and a type-aware input field (text for String, number input for Number, toggle for Boolean, textarea for JSON). Validation runs before the RPC call. The TUI uses the same pattern with `EnvironmentVariableDetailDialog`.

### Bottom detail pane for Connection References
Row click shows connection info, dependent flows, and orphan status in a bottom detail pane below the table. This is consistent with the Import Jobs pattern (XML log viewer) and works with the existing DataTable component. No layout changes needed.

### Sequential panel order within the phase
Connection References first (read-only, establishes solution filter), then Environment Variables (adds write operation, reuses solution filter). This avoids building the edit flow before the shared infrastructure is solid.

### No Application Service wrapper needed
Both `IConnectionReferenceService` and `IEnvironmentVariableService` are already clean domain services with the right abstraction level. RPC handlers call them directly via DI, same as `IImportJobService`. The enrichment logic (merging connection status from `IConnectionService`) lives in the RPC handler since it's a cross-service join specific to the API layer.

---

## File Structure

### Files to modify
| File | Change |
|------|--------|
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Add 6 RPC methods + DTOs for both panels |
| `src/PPDS.Extension/src/types.ts` | Add ConnectionReference and EnvironmentVariable DTO interfaces |
| `src/PPDS.Extension/src/daemonClient.ts` | Add 6 daemon client methods |
| `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` | Add message types for both panels |
| `src/PPDS.Extension/esbuild.js` | Add 4 build entries (2 JS + 2 CSS) |
| `src/PPDS.Extension/src/extension.ts` | Register 4 commands (open + openForEnv × 2 panels) |
| `src/PPDS.Extension/package.json` | Add command contributions + menu items |
| `src/PPDS.Cli/Tui/TuiShell.cs` | Add 2 menu items for new screens |

### Files to create
| File | Purpose |
|------|---------|
| **Shared** | |
| `src/PPDS.Extension/src/panels/webview/shared/solution-filter.ts` | Shared solution filter dropdown component |
| **Connection References** | |
| `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/connection-references-panel.css` | Panel-specific CSS |
| `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` | TUI screen |
| `src/PPDS.Mcp/Tools/ConnectionReferencesListTool.cs` | MCP list tool |
| `src/PPDS.Mcp/Tools/ConnectionReferencesGetTool.cs` | MCP get tool (with flows) |
| `src/PPDS.Mcp/Tools/ConnectionReferencesAnalyzeTool.cs` | MCP analyze tool (orphan detection) |
| **Environment Variables** | |
| `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/environment-variables-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/environment-variables-panel.css` | Panel-specific CSS |
| `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` | TUI screen |
| `src/PPDS.Mcp/Tools/EnvironmentVariablesListTool.cs` | MCP list tool |
| `src/PPDS.Mcp/Tools/EnvironmentVariablesGetTool.cs` | MCP get tool |
| `src/PPDS.Mcp/Tools/EnvironmentVariablesSetTool.cs` | MCP set tool (write operation) |

---

## Chunk 1: Connection References — RPC Layer

### Task 1: Add Connection References RPC endpoints and DTOs

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add DTO classes at the end of the file (alongside existing DTOs)**

```csharp
// ── Connection References DTOs ──────────────────────────────────────────

/// <summary>
/// Response for connectionReferences/list method.
/// </summary>
public class ConnectionReferencesListResponse
{
    [JsonPropertyName("references")]
    public List<ConnectionReferenceInfoDto> References { get; set; } = [];
}

/// <summary>
/// Response for connectionReferences/get method.
/// </summary>
public class ConnectionReferencesGetResponse
{
    [JsonPropertyName("reference")]
    public ConnectionReferenceDetailDto Reference { get; set; } = null!;
}

/// <summary>
/// Response for connectionReferences/analyze method.
/// </summary>
public class ConnectionReferencesAnalyzeResponse
{
    [JsonPropertyName("orphanedReferences")]
    public List<OrphanedReferenceDto> OrphanedReferences { get; set; } = [];

    [JsonPropertyName("orphanedFlows")]
    public List<OrphanedFlowDto> OrphanedFlows { get; set; } = [];

    [JsonPropertyName("totalReferences")]
    public int TotalReferences { get; set; }

    [JsonPropertyName("totalFlows")]
    public int TotalFlows { get; set; }
}

public class ConnectionReferenceInfoDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    public string? ConnectorId { get; set; }

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("modifiedOn")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("connectionStatus")]
    public string ConnectionStatus { get; set; } = "N/A";

    [JsonPropertyName("connectorDisplayName")]
    public string? ConnectorDisplayName { get; set; }
}

public class ConnectionReferenceDetailDto : ConnectionReferenceInfoDto
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isBound")]
    public bool IsBound { get; set; }

    [JsonPropertyName("createdOn")]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("flows")]
    public List<FlowReferenceDto> Flows { get; set; } = [];

    [JsonPropertyName("connectionOwner")]
    public string? ConnectionOwner { get; set; }

    [JsonPropertyName("connectionIsShared")]
    public bool? ConnectionIsShared { get; set; }
}

public class FlowReferenceDto
{
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

public class OrphanedReferenceDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    public string? ConnectorId { get; set; }
}

public class OrphanedFlowDto
{
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("missingReference")]
    public string? MissingReference { get; set; }
}
```

- [ ] **Step 2: Add `connectionReferences/list` RPC method**

Add after the existing `importJobs/get` method block:

```csharp
// ── Connection References ─────────────────────────────────────────────

/// <summary>
/// Lists connection references, optionally filtered by solution.
/// Enriches with connection status from Power Platform API (graceful degradation for SPN).
/// </summary>
[JsonRpcMethod("connectionReferences/list")]
public async Task<ConnectionReferencesListResponse> ConnectionReferencesListAsync(
    string? solutionId = null,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var crService = sp.GetRequiredService<IConnectionReferenceService>();
        var references = await crService.ListAsync(solutionName: solutionId, cancellationToken: ct);

        // Try to enrich with connection status from Connections API (BAPI)
        Dictionary<string, ConnectionInfo>? connectionMap = null;
        try
        {
            var connService = sp.GetRequiredService<IConnectionService>();
            var connections = await connService.ListAsync(cancellationToken: ct);
            connectionMap = connections
                .Where(c => c.ConnectionId != null)
                .ToDictionary(c => c.ConnectionId!, c => c, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connections API unavailable (likely SPN auth) — connection status will show N/A");
        }

        return new ConnectionReferencesListResponse
        {
            References = references.Select(r => MapConnectionReferenceToDto(r, connectionMap)).ToList()
        };
    }, cancellationToken);
}
```

- [ ] **Step 3: Add `connectionReferences/get` RPC method**

```csharp
/// <summary>
/// Gets a single connection reference with dependent flows and connection details.
/// </summary>
[JsonRpcMethod("connectionReferences/get")]
public async Task<ConnectionReferencesGetResponse> ConnectionReferencesGetAsync(
    string logicalName,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(logicalName))
        throw new RpcException(ErrorCodes.Validation.RequiredField, "logicalName is required");

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var crService = sp.GetRequiredService<IConnectionReferenceService>();
        var reference = await crService.GetAsync(logicalName, ct);
        if (reference == null)
            throw new RpcException(ErrorCodes.Operation.NotFound, $"Connection reference '{logicalName}' not found");

        var flows = await crService.GetFlowsUsingAsync(logicalName, ct);

        // Try to get connection detail
        ConnectionInfo? connectionInfo = null;
        if (!string.IsNullOrEmpty(reference.ConnectionId))
        {
            try
            {
                var connService = sp.GetRequiredService<IConnectionService>();
                connectionInfo = await connService.GetAsync(reference.ConnectionId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connections API unavailable — connection detail will be partial");
            }
        }

        var dto = MapConnectionReferenceToDetailDto(reference, flows, connectionInfo);
        return new ConnectionReferencesGetResponse { Reference = dto };
    }, cancellationToken);
}
```

- [ ] **Step 4: Add `connectionReferences/analyze` RPC method**

```csharp
/// <summary>
/// Analyzes connection references for orphaned references and flows.
/// </summary>
[JsonRpcMethod("connectionReferences/analyze")]
public async Task<ConnectionReferencesAnalyzeResponse> ConnectionReferencesAnalyzeAsync(
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var crService = sp.GetRequiredService<IConnectionReferenceService>();
        var analysis = await crService.AnalyzeAsync(cancellationToken: ct);

        var response = new ConnectionReferencesAnalyzeResponse
        {
            TotalReferences = analysis.Relationships.Count(r =>
                r.Type != RelationshipType.OrphanedFlow),
            TotalFlows = analysis.Relationships.Count(r =>
                r.Type != RelationshipType.OrphanedConnectionReference),
        };

        foreach (var rel in analysis.Relationships)
        {
            if (rel.Type == RelationshipType.OrphanedConnectionReference)
            {
                response.OrphanedReferences.Add(new OrphanedReferenceDto
                {
                    LogicalName = rel.ConnectionReferenceLogicalName ?? "",
                    DisplayName = rel.ConnectionReferenceDisplayName,
                    ConnectorId = rel.ConnectorId,
                });
            }
            else if (rel.Type == RelationshipType.OrphanedFlow)
            {
                response.OrphanedFlows.Add(new OrphanedFlowDto
                {
                    UniqueName = rel.FlowUniqueName ?? "",
                    DisplayName = rel.FlowDisplayName,
                    MissingReference = rel.ConnectionReferenceLogicalName,
                });
            }
        }

        return response;
    }, cancellationToken);
}
```

- [ ] **Step 5: Add private mapper methods**

```csharp
private static ConnectionReferenceInfoDto MapConnectionReferenceToDto(
    ConnectionReferenceInfo r,
    Dictionary<string, ConnectionInfo>? connectionMap)
{
    var status = "N/A";
    string? connectorDisplayName = null;

    if (connectionMap != null && !string.IsNullOrEmpty(r.ConnectionId) &&
        connectionMap.TryGetValue(r.ConnectionId, out var conn))
    {
        status = conn.Status.ToString();
        connectorDisplayName = conn.ConnectorDisplayName;
    }

    return new ConnectionReferenceInfoDto
    {
        LogicalName = r.LogicalName,
        DisplayName = r.DisplayName,
        ConnectorId = r.ConnectorId,
        ConnectionId = r.ConnectionId,
        IsManaged = r.IsManaged,
        ModifiedOn = r.ModifiedOn?.ToString("o"),
        ConnectionStatus = status,
        ConnectorDisplayName = connectorDisplayName,
    };
}

private static ConnectionReferenceDetailDto MapConnectionReferenceToDetailDto(
    ConnectionReferenceInfo r,
    List<FlowInfo> flows,
    ConnectionInfo? connectionInfo)
{
    var dto = new ConnectionReferenceDetailDto
    {
        LogicalName = r.LogicalName,
        DisplayName = r.DisplayName,
        Description = r.Description,
        ConnectorId = r.ConnectorId,
        ConnectionId = r.ConnectionId,
        IsManaged = r.IsManaged,
        IsBound = r.IsBound,
        ModifiedOn = r.ModifiedOn?.ToString("o"),
        CreatedOn = r.CreatedOn?.ToString("o"),
        ConnectionStatus = connectionInfo?.Status.ToString() ?? "N/A",
        ConnectorDisplayName = connectionInfo?.ConnectorDisplayName,
        ConnectionOwner = connectionInfo?.CreatedBy,
        ConnectionIsShared = connectionInfo?.IsShared,
        Flows = flows.Select(f => new FlowReferenceDto
        {
            UniqueName = f.UniqueName,
            DisplayName = f.DisplayName,
            State = f.State.ToString(),
        }).ToList(),
    };
    return dto;
}
```

- [ ] **Step 6: Build to verify compilation**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded. May need to add `using` directives for `IConnectionReferenceService`, `IConnectionService`, `ConnectionReferenceInfo`, `ConnectionInfo`, `FlowInfo`, `FlowConnectionAnalysis`, `RelationshipType`.

---

## Chunk 2: Connection References — MCP Tools

### Task 2: Create MCP tools for Connection References

**Files:**
- Create: `src/PPDS.Mcp/Tools/ConnectionReferencesListTool.cs`
- Create: `src/PPDS.Mcp/Tools/ConnectionReferencesGetTool.cs`
- Create: `src/PPDS.Mcp/Tools/ConnectionReferencesAnalyzeTool.cs`

- [ ] **Step 1: Create ConnectionReferencesListTool.cs**

Follow the `ImportJobsListTool.cs` pattern exactly. Key differences:
- Tool name: `ppds_connection_references_list`
- Description: "List connection references in the current environment. Shows connector bindings and connection status. Optionally filter by solution name."
- Parameters: `string? solutionId = null` (with description "Solution unique name to filter by")
- Uses `McpToolContext` to create service provider
- Gets `IConnectionReferenceService` and optionally `IConnectionService` (with try/catch for SPN graceful degradation — same pattern as RPC)
- Returns `ConnectionReferencesListResult` with `List<ConnectionReferenceSummary>` items

Result DTO fields: logicalName, displayName, connectorId, connectionId, isManaged, isBound, connectionStatus, connectorDisplayName

- [ ] **Step 2: Create ConnectionReferencesGetTool.cs**

- Tool name: `ppds_connection_references_get`
- Description: "Get full details of a specific connection reference including dependent flows and connection info. Use the logicalName from ppds_connection_references_list."
- Parameters: `string logicalName` (required)
- Gets `IConnectionReferenceService.GetAsync` + `GetFlowsUsingAsync` + `IConnectionService.GetAsync` (with graceful degradation)
- Returns `ConnectionReferencesGetResult` with full detail + flows list

- [ ] **Step 3: Create ConnectionReferencesAnalyzeTool.cs**

- Tool name: `ppds_connection_references_analyze`
- Description: "Analyze connection references for orphaned references (not used by any flow) and orphaned flows (referencing missing connection references). Useful for deployment cleanup."
- No required parameters
- Gets `IConnectionReferenceService.AnalyzeAsync`
- Returns `ConnectionReferencesAnalyzeResult` with orphanedReferences, orphanedFlows, summary counts

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -v q`

---

## Chunk 3: Connection References — TUI Screen

### Task 3: Create ConnectionReferencesScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs`
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs` — add menu item

- [ ] **Step 1: Create ConnectionReferencesScreen.cs**

Follow `ImportJobsScreen.cs` pattern. Key structure:

```csharp
internal sealed class ConnectionReferencesScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<ConnectionReferenceInfo> _references = [];
    private string? _solutionFilter;

    public override string Title => "Connection References";
}
```

Table columns: Display Name, Logical Name, Connector, Status (N/A if BAPI unavailable), Managed, Modified On

Hotkeys:
- `Ctrl+R` → Refresh
- `Enter` → Detail dialog (show connection info, flows list, orphan status)
- `Ctrl+A` → Analyze dialog (orphan detection summary)
- `Ctrl+F` → Solution filter dialog (list solutions, pick one or clear)
- `Ctrl+O` → Open in Maker

Data loading: call `IConnectionReferenceService.ListAsync(solutionName: _solutionFilter)`, then try `IConnectionService.ListAsync()` for status enrichment (catch and log on failure).

Status format: "12 connection references — 10 bound — 2 unbound" or "12 connection references (filtered: MySolution)"

- [ ] **Step 2: Create detail dialog**

On `Enter` / cell activated:
- Show `Dialog` with connection reference details
- Sections: Connection Info (status, owner, shared/personal), Dependent Flows (list with state), Orphan indicator
- Load flows via `IConnectionReferenceService.GetFlowsUsingAsync(logicalName)`

- [ ] **Step 3: Create analyze dialog**

On `Ctrl+A`:
- Call `IConnectionReferenceService.AnalyzeAsync()`
- Show `Dialog` with orphan summary: count of orphaned references, count of orphaned flows, list of each

- [ ] **Step 4: Create solution filter dialog**

On `Ctrl+F`:
- Call `ISolutionService.ListAsync(includeManaged: false)`
- Show `Dialog` with solution list for selection
- "All" option to clear filter
- Set `_solutionFilter` and reload data

- [ ] **Step 5: Add menu item in TuiShell.cs**

Add after the "Import Jobs" menu item (around line 283):

```csharp
new("Connection References", "View connection references", () => NavigateToConnectionReferences()),
```

Add `NavigateToConnectionReferences()` method following `NavigateToImportJobs()` pattern.

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`

---

## Chunk 4: Connection References — VS Code Extension (TypeScript)

### Task 4: Add TypeScript types and daemon client methods

**Files:**
- Modify: `src/PPDS.Extension/src/types.ts`
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Add DTO interfaces to types.ts**

Add after the Import Jobs section:

```typescript
// ── Connection References ───────────────────────────────────────────────

export interface ConnectionReferencesListResponse {
    references: ConnectionReferenceInfoDto[];
}

export interface ConnectionReferencesGetResponse {
    reference: ConnectionReferenceDetailDto;
}

export interface ConnectionReferencesAnalyzeResponse {
    orphanedReferences: OrphanedReferenceDto[];
    orphanedFlows: OrphanedFlowDto[];
    totalReferences: number;
    totalFlows: number;
}

export interface ConnectionReferenceInfoDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
    connectionId: string | null;
    isManaged: boolean;
    modifiedOn: string | null;
    connectionStatus: string;
    connectorDisplayName: string | null;
}

export interface ConnectionReferenceDetailDto extends ConnectionReferenceInfoDto {
    description: string | null;
    isBound: boolean;
    createdOn: string | null;
    flows: FlowReferenceDto[];
    connectionOwner: string | null;
    connectionIsShared: boolean | null;
}

export interface FlowReferenceDto {
    uniqueName: string;
    displayName: string | null;
    state: string | null;
}

export interface OrphanedReferenceDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
}

export interface OrphanedFlowDto {
    uniqueName: string;
    displayName: string | null;
    missingReference: string | null;
}
```

- [ ] **Step 2: Add daemon client methods to daemonClient.ts**

Add after the `importJobsGet` method, following the same pattern:

```typescript
// ── Connection References ───────────────────────────────────────────────

async connectionReferencesList(solutionId?: string, environmentUrl?: string): Promise<ConnectionReferencesListResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (solutionId !== undefined) params.solutionId = solutionId;
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling connectionReferences/list...');
    const result = await this.connection!.sendRequest<ConnectionReferencesListResponse>('connectionReferences/list', params);
    this.log.debug(`Got ${result.references.length} connection references`);
    return result;
}

async connectionReferencesGet(logicalName: string, environmentUrl?: string): Promise<ConnectionReferencesGetResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { logicalName };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling connectionReferences/get for ${logicalName}...`);
    return await this.connection!.sendRequest<ConnectionReferencesGetResponse>('connectionReferences/get', params);
}

async connectionReferencesAnalyze(environmentUrl?: string): Promise<ConnectionReferencesAnalyzeResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling connectionReferences/analyze...');
    return await this.connection!.sendRequest<ConnectionReferencesAnalyzeResponse>('connectionReferences/analyze', params);
}
```

- [ ] **Step 3: Add import for new response types in daemonClient.ts**

Ensure the import statement at the top of `daemonClient.ts` includes the new types.

- [ ] **Step 4: Typecheck to verify**

Run: `npm run typecheck` from `src/PPDS.Extension/`

### Task 5: Create shared solution filter component

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/shared/solution-filter.ts`

- [ ] **Step 1: Create the SolutionFilter class**

```typescript
import { escapeHtml, escapeAttr } from './utils.js';

export interface SolutionOption {
    id: string;
    uniqueName: string;
    friendlyName: string;
}

export interface SolutionFilterOptions {
    /** Called when the user selects a solution or clears the filter */
    onChange: (solutionId: string | null) => void;
    /** VS Code API for state persistence */
    getState: () => Record<string, unknown> | undefined;
    setState: (state: Record<string, unknown>) => void;
    /** Storage key for persisted selection (default: 'solutionFilter') */
    storageKey?: string;
}

export class SolutionFilter {
    private container: HTMLElement;
    private options: SolutionFilterOptions;
    private solutions: SolutionOption[] = [];
    private selectedId: string | null = null;
    private storageKey: string;

    constructor(container: HTMLElement, options: SolutionFilterOptions) {
        this.container = container;
        this.options = options;
        this.storageKey = options.storageKey ?? 'solutionFilter';
        // Restore persisted selection
        const state = options.getState();
        if (state && typeof state[this.storageKey] === 'string') {
            this.selectedId = state[this.storageKey] as string;
        }
        this.render();
    }

    /** Load solutions from the host and render the dropdown */
    setSolutions(solutions: SolutionOption[]): void {
        this.solutions = solutions;
        this.render();
    }

    /** Get the currently selected solution ID (null = all) */
    getSelectedId(): string | null {
        return this.selectedId;
    }

    private render(): void {
        // Renders a <select> dropdown with "All Solutions" + solution list
        // Uses escapeHtml/escapeAttr for safety (Constitution S1)
        // Persists selection to vscode state on change
    }

    private persist(): void {
        const state = this.options.getState() ?? {};
        if (this.selectedId) {
            state[this.storageKey] = this.selectedId;
        } else {
            delete state[this.storageKey];
        }
        this.options.setState(state);
    }
}
```

The render method creates a `<select>` element with an "All Solutions" option and one `<option>` per solution. On change, it updates `selectedId`, persists, and calls `onChange`.

- [ ] **Step 2: Verify build** — this is a shared module, no standalone build needed; it will be imported by panel scripts.

### Task 6: Create Connection References webview panel

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts`
- Create: `src/PPDS.Extension/src/panels/styles/connection-references-panel.css`
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` — add message types

- [ ] **Step 1: Add message types to message-types.ts**

```typescript
// ── Connection References ───────────────────────────────────────────────

export interface ConnectionReferenceViewDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
    connectionId: string | null;
    isManaged: boolean;
    modifiedOn: string | null;
    connectionStatus: string;
    connectorDisplayName: string | null;
}

export interface ConnectionReferenceDetailViewDto extends ConnectionReferenceViewDto {
    description: string | null;
    isBound: boolean;
    createdOn: string | null;
    flows: { uniqueName: string; displayName: string | null; state: string | null }[];
    connectionOwner: string | null;
    connectionIsShared: boolean | null;
}

export interface ConnectionReferencesAnalyzeViewDto {
    orphanedReferences: { logicalName: string; displayName: string | null; connectorId: string | null }[];
    orphanedFlows: { uniqueName: string; displayName: string | null; missingReference: string | null }[];
    totalReferences: number;
    totalFlows: number;
}

export type ConnectionReferencesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectReference'; logicalName: string }
    | { command: 'analyze' }
    | { command: 'filterBySolution'; solutionId: string | null }
    | { command: 'requestSolutionList' }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

export type ConnectionReferencesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string; envColor: string }
    | { command: 'loading' }
    | { command: 'connectionReferencesLoaded'; references: ConnectionReferenceViewDto[] }
    | { command: 'connectionReferenceDetailLoaded'; reference: ConnectionReferenceDetailViewDto }
    | { command: 'analyzeResult'; result: ConnectionReferencesAnalyzeViewDto }
    | { command: 'solutionListLoaded'; solutions: { id: string; uniqueName: string; friendlyName: string }[] }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 2: Create connection-references-panel.ts webview script**

Follow `import-jobs-panel.ts` pattern:
- Initialize `DataTable<ConnectionReferenceViewDto>` with columns: Display Name, Logical Name, Connector, Status (color-coded badge), Managed, Modified On
- Default sort: logicalName ascending
- Initialize `SolutionFilter` in the toolbar area
- On `ready`: post `requestSolutionList`, then data loads
- On row click: post `selectReference` with logicalName
- On `connectionReferenceDetailLoaded`: render bottom detail pane with connection info, flows list, orphan indicator
- On `analyzeResult`: render analyze modal/overlay with orphan counts and lists
- Status bar format: "12 connection references — 10 bound — 2 unbound"
- Connection status badges: "Connected" (green), "Error" (red), "N/A" (gray)

- [ ] **Step 3: Create connection-references-panel.css**

Import shared.css. Add:
- Connection status badge styles (reuse status-badge pattern from import-jobs)
- Detail pane styles (reuse import-jobs detail pane pattern)
- Solution filter dropdown styling
- Analyze result overlay/modal styling

- [ ] **Step 4: Add esbuild entries**

Add to `esbuild.js` after the import-jobs-panel entries:

```javascript
// Connection References panel webview (browser, IIFE)
{
    entryPoints: ['src/panels/webview/connection-references-panel.ts'],
    bundle: true,
    format: 'iife',
    minify: production,
    sourcemap: !production,
    sourcesContent: false,
    platform: 'browser',
    outfile: 'dist/connection-references-panel.js',
    logLevel: 'warning',
},
```

And CSS entry:

```javascript
// Connection References panel CSS
{
    entryPoints: ['src/panels/styles/connection-references-panel.css'],
    bundle: true,
    minify: production,
    outfile: 'dist/connection-references-panel.css',
    logLevel: 'warning',
},
```

### Task 7: Create ConnectionReferencesPanel host-side panel

**Files:**
- Create: `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts`

- [ ] **Step 1: Create ConnectionReferencesPanel.ts**

Follow `ImportJobsPanel.ts` pattern exactly:
- `ConnectionReferencesPanel extends WebviewPanelBase<ConnectionReferencesPanelWebviewToHost, ConnectionReferencesPanelHostToWebview>`
- Static `show(extensionUri, daemon, envUrl?, envDisplayName?)` factory
- Static instance tracking with `MAX_PANELS = 5`
- `viewType = 'ppds.connectionReferences'`
- `handleMessage()` dispatches on command:
  - `ready` → `initialize()` → load auth context → resolve environment → `loadConnectionReferences()`
  - `refresh` → `loadConnectionReferences()`
  - `selectReference` → `loadConnectionReferenceDetail(logicalName)`
  - `analyze` → `runAnalysis()`
  - `filterBySolution` → store solutionId → `loadConnectionReferences()`
  - `requestSolutionList` → `loadSolutionList()`
  - `requestEnvironmentList` → show QuickPick
  - `openInMaker` → open URL
  - `copyToClipboard` → clipboard
  - `webviewError` → log
- `loadConnectionReferences()`: calls `daemon.connectionReferencesList(solutionId, environmentUrl)`, posts `connectionReferencesLoaded`
- `loadConnectionReferenceDetail(logicalName)`: calls `daemon.connectionReferencesGet(logicalName, environmentUrl)`, posts `connectionReferenceDetailLoaded`
- `runAnalysis()`: calls `daemon.connectionReferencesAnalyze(environmentUrl)`, posts `analyzeResult`
- `loadSolutionList()`: calls `daemon.solutionsList(undefined, false, environmentUrl)`, posts `solutionListLoaded`
- HTML template loads `connection-references-panel.js` and `connection-references-panel.css`

- [ ] **Step 2: Typecheck**

Run: `npm run typecheck` from `src/PPDS.Extension/`

### Task 8: Register Connection References panel in extension

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts`
- Modify: `src/PPDS.Extension/package.json`

- [ ] **Step 1: Add commands to package.json**

In the `commands` array:
```json
{
    "command": "ppds.openConnectionReferences",
    "title": "Open Connection References",
    "category": "PPDS",
    "icon": "$(plug)"
},
{
    "command": "ppds.openConnectionReferencesForEnv",
    "title": "Open Connection References",
    "icon": "$(plug)"
}
```

In the `view/item/context` menus array, add after the importJobs entry:
```json
{
    "command": "ppds.openConnectionReferencesForEnv",
    "when": "view == ppds.profiles && viewItem == environment",
    "group": "env-tools@4"
}
```

- [ ] **Step 2: Register commands in extension.ts**

Import `ConnectionReferencesPanel` and register both commands following the Import Jobs pattern:
```typescript
vscode.commands.registerCommand('ppds.openConnectionReferences', () => {
    ConnectionReferencesPanel.show(context.extensionUri, client);
});

vscode.commands.registerCommand('ppds.openConnectionReferencesForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
    ConnectionReferencesPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
}));
```

- [ ] **Step 3: Build and typecheck**

Run: `npm run build && npm run typecheck` from `src/PPDS.Extension/`

- [ ] **Step 4: Verify .NET build is still clean**

Run: `dotnet build PPDS.sln -v q`

---

## Chunk 5: Environment Variables — RPC Layer

### Task 9: Add Environment Variables RPC endpoints and DTOs

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add DTO classes**

```csharp
// ── Environment Variables DTOs ──────────────────────────────────────────

public class EnvironmentVariablesListResponse
{
    [JsonPropertyName("variables")]
    public List<EnvironmentVariableInfoDto> Variables { get; set; } = [];
}

public class EnvironmentVariablesGetResponse
{
    [JsonPropertyName("variable")]
    public EnvironmentVariableDetailDto Variable { get; set; } = null!;
}

public class EnvironmentVariablesSetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class EnvironmentVariableInfoDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("currentValue")]
    public string? CurrentValue { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("modifiedOn")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("hasOverride")]
    public bool HasOverride { get; set; }

    [JsonPropertyName("isMissing")]
    public bool IsMissing { get; set; }
}

public class EnvironmentVariableDetailDto : EnvironmentVariableInfoDto
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdOn")]
    public string? CreatedOn { get; set; }
}
```

- [ ] **Step 2: Add `environmentVariables/list` RPC method**

```csharp
// ── Environment Variables ─────────────────────────────────────────────

[JsonRpcMethod("environmentVariables/list")]
public async Task<EnvironmentVariablesListResponse> EnvironmentVariablesListAsync(
    string? solutionId = null,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var evService = sp.GetRequiredService<IEnvironmentVariableService>();
        var variables = await evService.ListAsync(solutionName: solutionId, cancellationToken: ct);

        return new EnvironmentVariablesListResponse
        {
            Variables = variables.Select(MapEnvironmentVariableToDto).ToList()
        };
    }, cancellationToken);
}
```

- [ ] **Step 3: Add `environmentVariables/get` RPC method**

```csharp
[JsonRpcMethod("environmentVariables/get")]
public async Task<EnvironmentVariablesGetResponse> EnvironmentVariablesGetAsync(
    string schemaName,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(schemaName))
        throw new RpcException(ErrorCodes.Validation.RequiredField, "schemaName is required");

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var evService = sp.GetRequiredService<IEnvironmentVariableService>();
        var variable = await evService.GetAsync(schemaName, ct);
        if (variable == null)
            throw new RpcException(ErrorCodes.Operation.NotFound, $"Environment variable '{schemaName}' not found");

        return new EnvironmentVariablesGetResponse
        {
            Variable = MapEnvironmentVariableToDetailDto(variable)
        };
    }, cancellationToken);
}
```

- [ ] **Step 4: Add `environmentVariables/set` RPC method**

```csharp
[JsonRpcMethod("environmentVariables/set")]
public async Task<EnvironmentVariablesSetResponse> EnvironmentVariablesSetAsync(
    string schemaName,
    string value,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(schemaName))
        throw new RpcException(ErrorCodes.Validation.RequiredField, "schemaName is required");
    if (value == null)
        throw new RpcException(ErrorCodes.Validation.RequiredField, "value is required");

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var evService = sp.GetRequiredService<IEnvironmentVariableService>();
        var success = await evService.SetValueAsync(schemaName, value, ct);
        return new EnvironmentVariablesSetResponse { Success = success };
    }, cancellationToken);
}
```

- [ ] **Step 5: Add private mapper methods**

```csharp
private static EnvironmentVariableInfoDto MapEnvironmentVariableToDto(EnvironmentVariableInfo v)
{
    return new EnvironmentVariableInfoDto
    {
        SchemaName = v.SchemaName,
        DisplayName = v.DisplayName,
        Type = v.Type,
        DefaultValue = v.DefaultValue,
        CurrentValue = v.CurrentValue,
        IsManaged = v.IsManaged,
        IsRequired = v.IsRequired,
        ModifiedOn = v.ModifiedOn?.ToString("o"),
        HasOverride = v.CurrentValue != null && v.CurrentValue != v.DefaultValue,
        IsMissing = v.IsRequired && v.CurrentValue == null && v.DefaultValue == null,
    };
}

private static EnvironmentVariableDetailDto MapEnvironmentVariableToDetailDto(EnvironmentVariableInfo v)
{
    return new EnvironmentVariableDetailDto
    {
        SchemaName = v.SchemaName,
        DisplayName = v.DisplayName,
        Description = v.Description,
        Type = v.Type,
        DefaultValue = v.DefaultValue,
        CurrentValue = v.CurrentValue,
        IsManaged = v.IsManaged,
        IsRequired = v.IsRequired,
        ModifiedOn = v.ModifiedOn?.ToString("o"),
        CreatedOn = v.CreatedOn?.ToString("o"),
        HasOverride = v.CurrentValue != null && v.CurrentValue != v.DefaultValue,
        IsMissing = v.IsRequired && v.CurrentValue == null && v.DefaultValue == null,
    };
}
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`

---

## Chunk 6: Environment Variables — MCP Tools

### Task 10: Create MCP tools for Environment Variables

**Files:**
- Create: `src/PPDS.Mcp/Tools/EnvironmentVariablesListTool.cs`
- Create: `src/PPDS.Mcp/Tools/EnvironmentVariablesGetTool.cs`
- Create: `src/PPDS.Mcp/Tools/EnvironmentVariablesSetTool.cs`

- [ ] **Step 1: Create EnvironmentVariablesListTool.cs**

- Tool name: `ppds_environment_variables_list`
- Description: "List environment variable definitions and their current values. Shows default vs current values, type, and override status. Optionally filter by solution name."
- Parameters: `string? solutionId = null`
- Result DTO fields: schemaName, displayName, type, defaultValue, currentValue, isManaged, isRequired, hasOverride, isMissing

- [ ] **Step 2: Create EnvironmentVariablesGetTool.cs**

- Tool name: `ppds_environment_variables_get`
- Description: "Get full details of a specific environment variable including description, type, and values. Use the schemaName from ppds_environment_variables_list."
- Parameters: `string schemaName` (required)

- [ ] **Step 3: Create EnvironmentVariablesSetTool.cs**

- Tool name: `ppds_environment_variables_set`
- Description: "Set the current value of an environment variable. Creates the value record if none exists. AI agents can use this to fix misconfigurations during deployment troubleshooting."
- Parameters: `string schemaName` (required), `string value` (required)
- Returns success boolean

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -v q`

---

## Chunk 7: Environment Variables — TUI Screen

### Task 11: Create EnvironmentVariablesScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs`
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs` — add menu item

- [ ] **Step 1: Create EnvironmentVariablesScreen.cs**

Follow `ImportJobsScreen.cs` + `ConnectionReferencesScreen.cs` patterns.

Table columns: Schema Name, Display Name, Type, Default Value, Current Value, Managed, Modified On

Hotkeys:
- `Ctrl+R` → Refresh
- `Enter` → Detail/edit dialog
- `Ctrl+E` → Export deployment settings
- `Ctrl+F` → Solution filter dialog (reuse pattern from ConnectionReferencesScreen)
- `Ctrl+O` → Open in Maker

Status format: "15 environment variables — 3 overridden — 1 missing value"

Visual indicators in table:
- Override: current value differs from default (could use color/marker)
- Missing: required variable with no value and no default (warning color)

- [ ] **Step 2: Create detail/edit dialog**

On `Enter` / cell activated:
- Show `Dialog` with variable details: schema name, display name, description, type, default value
- Editable field for current value with type-aware validation:
  - String: `TextField`
  - Number: `TextField` with numeric validation
  - Boolean: `CheckBox` or RadioGroup (true/false)
  - JSON: `TextView` (multi-line) with JSON parse validation
  - DataSource / Secret: read-only display
- "Save" button calls `IEnvironmentVariableService.SetValueAsync()`
- "Cancel" dismisses without changes
- On save success: refresh the table row

- [ ] **Step 3: Create export dialog**

On `Ctrl+E`:
- Call `IEnvironmentVariableService.ExportAsync(solutionName: _solutionFilter)`
- Serialize to `.deploymentsettings.json` format
- Prompt for file path (use `SaveDialog` or write to current directory)
- Write file and show success message

- [ ] **Step 4: Add menu item in TuiShell.cs**

Add after the "Connection References" menu item:

```csharp
new("Environment Variables", "View and edit environment variables", () => NavigateToEnvironmentVariables()),
```

Add `NavigateToEnvironmentVariables()` method.

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`

---

## Chunk 8: Environment Variables — VS Code Extension (TypeScript)

### Task 12: Add TypeScript types and daemon client methods

**Files:**
- Modify: `src/PPDS.Extension/src/types.ts`
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Add DTO interfaces to types.ts**

```typescript
// ── Environment Variables ───────────────────────────────────────────────

export interface EnvironmentVariablesListResponse {
    variables: EnvironmentVariableInfoDto[];
}

export interface EnvironmentVariablesGetResponse {
    variable: EnvironmentVariableDetailDto;
}

export interface EnvironmentVariablesSetResponse {
    success: boolean;
}

export interface EnvironmentVariableInfoDto {
    schemaName: string;
    displayName: string | null;
    type: string;
    defaultValue: string | null;
    currentValue: string | null;
    isManaged: boolean;
    isRequired: boolean;
    modifiedOn: string | null;
    hasOverride: boolean;
    isMissing: boolean;
}

export interface EnvironmentVariableDetailDto extends EnvironmentVariableInfoDto {
    description: string | null;
    createdOn: string | null;
}
```

- [ ] **Step 2: Add daemon client methods**

```typescript
// ── Environment Variables ───────────────────────────────────────────────

async environmentVariablesList(solutionId?: string, environmentUrl?: string): Promise<EnvironmentVariablesListResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (solutionId !== undefined) params.solutionId = solutionId;
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling environmentVariables/list...');
    const result = await this.connection!.sendRequest<EnvironmentVariablesListResponse>('environmentVariables/list', params);
    this.log.debug(`Got ${result.variables.length} environment variables`);
    return result;
}

async environmentVariablesGet(schemaName: string, environmentUrl?: string): Promise<EnvironmentVariablesGetResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { schemaName };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling environmentVariables/get for ${schemaName}...`);
    return await this.connection!.sendRequest<EnvironmentVariablesGetResponse>('environmentVariables/get', params);
}

async environmentVariablesSet(schemaName: string, value: string, environmentUrl?: string): Promise<EnvironmentVariablesSetResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { schemaName, value };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling environmentVariables/set for ${schemaName}...`);
    return await this.connection!.sendRequest<EnvironmentVariablesSetResponse>('environmentVariables/set', params);
}
```

- [ ] **Step 3: Typecheck**

Run: `npm run typecheck` from `src/PPDS.Extension/`

### Task 13: Create Environment Variables webview panel

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/environment-variables-panel.ts`
- Create: `src/PPDS.Extension/src/panels/styles/environment-variables-panel.css`
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add message types to message-types.ts**

```typescript
// ── Environment Variables ────────────────────────────────────────────────

export interface EnvironmentVariableViewDto {
    schemaName: string;
    displayName: string | null;
    type: string;
    defaultValue: string | null;
    currentValue: string | null;
    isManaged: boolean;
    isRequired: boolean;
    modifiedOn: string | null;
    hasOverride: boolean;
    isMissing: boolean;
}

export interface EnvironmentVariableDetailViewDto extends EnvironmentVariableViewDto {
    description: string | null;
    createdOn: string | null;
}

export type EnvironmentVariablesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectVariable'; schemaName: string }
    | { command: 'editVariable'; schemaName: string }
    | { command: 'saveVariable'; schemaName: string; value: string }
    | { command: 'filterBySolution'; solutionId: string | null }
    | { command: 'requestSolutionList' }
    | { command: 'exportDeploymentSettings' }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

export type EnvironmentVariablesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string; envColor: string }
    | { command: 'loading' }
    | { command: 'environmentVariablesLoaded'; variables: EnvironmentVariableViewDto[] }
    | { command: 'environmentVariableDetailLoaded'; variable: EnvironmentVariableDetailViewDto }
    | { command: 'editVariableDialog'; variable: EnvironmentVariableDetailViewDto }
    | { command: 'variableSaved'; schemaName: string; success: boolean }
    | { command: 'solutionListLoaded'; solutions: { id: string; uniqueName: string; friendlyName: string }[] }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 2: Create environment-variables-panel.ts webview script**

Follow the connection-references-panel.ts pattern:
- Initialize `DataTable<EnvironmentVariableViewDto>` with columns: Schema Name, Display Name, Type, Default Value, Current Value (with override/missing indicators), Managed, Modified On
- Default sort: schemaName ascending
- Initialize `SolutionFilter` in toolbar
- Current Value column render: highlight if `hasOverride` (different color), warning icon if `isMissing`
- Edit button in row or action column → posts `editVariable`
- On `editVariableDialog`: render modal dialog with type-aware input:
  - String: `<input type="text">`
  - Number: `<input type="number">`
  - Boolean: `<select>` with true/false options
  - JSON: `<textarea>` with syntax validation on blur
  - DataSource / Secret: display-only with message
- Save button in dialog: validate, then post `saveVariable`
- On `variableSaved`: close dialog, refresh affected row
- Export button in toolbar → posts `exportDeploymentSettings`
- Status bar: "15 environment variables — 3 overridden — 1 missing"

- [ ] **Step 3: Create environment-variables-panel.css**

Import shared.css. Add:
- Override value highlight (e.g., subtle background tint)
- Missing value warning indicator (yellow/orange icon or border)
- Edit dialog modal styles
- Type-specific input field styling
- Export button styling

- [ ] **Step 4: Add esbuild entries**

```javascript
// Environment Variables panel webview (browser, IIFE)
{
    entryPoints: ['src/panels/webview/environment-variables-panel.ts'],
    bundle: true,
    format: 'iife',
    minify: production,
    sourcemap: !production,
    sourcesContent: false,
    platform: 'browser',
    outfile: 'dist/environment-variables-panel.js',
    logLevel: 'warning',
},
```

```javascript
// Environment Variables panel CSS
{
    entryPoints: ['src/panels/styles/environment-variables-panel.css'],
    bundle: true,
    minify: production,
    outfile: 'dist/environment-variables-panel.css',
    logLevel: 'warning',
},
```

### Task 14: Create EnvironmentVariablesPanel host-side panel

**Files:**
- Create: `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts`

- [ ] **Step 1: Create EnvironmentVariablesPanel.ts**

Follow `ConnectionReferencesPanel.ts` pattern:
- `EnvironmentVariablesPanel extends WebviewPanelBase<EnvironmentVariablesPanelWebviewToHost, EnvironmentVariablesPanelHostToWebview>`
- `viewType = 'ppds.environmentVariables'`
- `handleMessage()` dispatches on command:
  - `ready` → `initialize()` → load auth context → resolve environment → `loadEnvironmentVariables()`
  - `refresh` → `loadEnvironmentVariables()`
  - `selectVariable` → `loadEnvironmentVariableDetail(schemaName)`
  - `editVariable` → `loadEnvironmentVariableForEdit(schemaName)` (fetches detail, posts `editVariableDialog`)
  - `saveVariable` → `saveEnvironmentVariable(schemaName, value)` (calls `daemon.environmentVariablesSet()`, posts `variableSaved`, refreshes list)
  - `filterBySolution` → store solutionId → `loadEnvironmentVariables()`
  - `requestSolutionList` → `loadSolutionList()`
  - `exportDeploymentSettings` → prompt save location, call daemon, write file
  - `requestEnvironmentList` → show QuickPick
  - `openInMaker` → open URL
- `exportDeploymentSettings()`: For export, the host can call `daemon.environmentVariablesList()`, format as deployment settings JSON, and use `vscode.workspace.fs.writeFile()` with a save dialog

- [ ] **Step 2: Typecheck**

Run: `npm run typecheck` from `src/PPDS.Extension/`

### Task 15: Register Environment Variables panel in extension

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts`
- Modify: `src/PPDS.Extension/package.json`

- [ ] **Step 1: Add commands to package.json**

```json
{
    "command": "ppds.openEnvironmentVariables",
    "title": "Open Environment Variables",
    "category": "PPDS",
    "icon": "$(symbol-variable)"
},
{
    "command": "ppds.openEnvironmentVariablesForEnv",
    "title": "Open Environment Variables",
    "icon": "$(symbol-variable)"
}
```

Context menu:
```json
{
    "command": "ppds.openEnvironmentVariablesForEnv",
    "when": "view == ppds.profiles && viewItem == environment",
    "group": "env-tools@5"
}
```

- [ ] **Step 2: Register commands in extension.ts**

Import `EnvironmentVariablesPanel` and register both commands.

- [ ] **Step 3: Full build verification**

Run: `npm run build && npm run typecheck` from `src/PPDS.Extension/`
Run: `dotnet build PPDS.sln -v q`
Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

---

## Chunk 9: Integration Testing and Polish

### Task 16: End-to-end verification

- [ ] **Step 1: Run full quality gates**

```
dotnet build PPDS.sln -v q
dotnet test PPDS.sln --filter "Category!=Integration" -v q
npm run typecheck (from src/PPDS.Extension/)
npm run lint (from src/PPDS.Extension/)
npm run ext:test (from src/PPDS.Extension/)
```

- [ ] **Step 2: Verify extension builds and runs**

- Build extension: `npm run build`
- Launch extension in development mode
- Verify both panels appear in command palette
- Verify panels appear in environment context menu

- [ ] **Step 3: TUI verification**

- Launch TUI: `ppds tui`
- Verify "Connection References" and "Environment Variables" appear in menu
- Navigate to each screen, verify layout and hotkeys

- [ ] **Step 4: MCP verification**

- Verify tools are registered: check MCP tool discovery
- Test `ppds_connection_references_list`, `ppds_environment_variables_list`
- Test `ppds_environment_variables_set` with a test variable

---

## Summary

| Chunk | Focus | Tasks | Key Risk |
|-------|-------|-------|----------|
| 1 | CR RPC | T1 (3 endpoints + DTOs + mappers) | SPN graceful degradation logic |
| 2 | CR MCP | T2 (3 tools) | Duplicating SPN degradation from RPC |
| 3 | CR TUI | T3 (screen + 3 dialogs + menu) | Solution filter dialog pattern |
| 4 | CR VS Code | T4-T8 (types, client, shared filter, webview, host, registration) | Shared solution filter component design |
| 5 | EV RPC | T9 (3 endpoints + DTOs + mappers) | Write operation validation |
| 6 | EV MCP | T10 (3 tools including write) | Set tool requires clear description for AI safety |
| 7 | EV TUI | T11 (screen + edit dialog + export + menu) | Type-aware edit validation in TUI |
| 8 | EV VS Code | T12-T15 (types, client, webview, host, registration) | Edit dialog modal UX |
| 9 | Integration | T16 (gates, verify all surfaces) | Cross-surface consistency |

**Total new files:** ~18
**Total modified files:** ~8
**Estimated acceptance criteria coverage:** AC-CR-01 through AC-CR-10, AC-EV-01 through AC-EV-10
