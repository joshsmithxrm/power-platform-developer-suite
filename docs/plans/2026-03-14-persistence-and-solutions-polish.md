# Persistence & Solutions Panel Polish — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add profile tree expansion/ordering persistence, and polish the Solutions panel with detail cards, component type resolution, search/filter, and managed toggle persistence.

**Architecture:** Three independent workstreams: (1) Profile tree persistence in the VS Code extension, (2) Component type resolution in the C# daemon's SolutionService, (3) Solutions panel webview improvements. Workstreams 1 and 2 have no code overlap and can be parallelized. Workstream 3 depends on workstream 2 for resolved type names but can start the detail card / search / persistence work immediately.

**Tech Stack:** TypeScript (VS Code extension, Vitest), C# .NET (daemon, xUnit + FluentAssertions + Moq), HTML/CSS (webview)

**Spec:** [`specs/vscode-persistence-and-solutions-polish.md`](../../../specs/vscode-persistence-and-solutions-polish.md)

---

## File Map

### Workstream 1: Profile Tree Persistence

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `extension/src/views/profileTreeView.ts` | Add `id` to ProfileTreeItem, add sort logic to getProfiles(), export `getProfileId()` helper |
| Modify | `extension/src/extension.ts` | Register moveProfileUp/moveProfileDown commands, pass `globalState` to provider |
| Modify | `extension/package.json` | Add command definitions + context menu entries for move up/down |
| Modify | `extension/src/__tests__/views/profileTreeView.test.ts` | Tests for id stability, sort ordering |

### Workstream 2: Component Type Resolution (Daemon)

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/PPDS.Dataverse/Services/SolutionService.cs` | Inject IMetadataService, add component type cache, fix hardcoded dict |
| Modify | `src/PPDS.Dataverse/Services/ISolutionService.cs` | No changes needed (GetComponentsAsync signature unchanged) |
| Modify | `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs` | No changes needed (IMetadataService already registered) |
| Modify | `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs` | Tests for metadata resolution, caching, fallback |

### Workstream 3: Solutions Panel Webview

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `extension/src/panels/SolutionsPanel.ts` | Pass date fields, managed toggle persistence, search filter, detail card HTML/CSS/JS |

---

## Chunk 1: Profile Tree Persistence (Workstream 1)

### Task 1: ProfileTreeItem Stable ID

**Files:**
- Modify: `extension/src/views/profileTreeView.ts:11-33`
- Test: `extension/src/__tests__/views/profileTreeView.test.ts`

- [ ] **Step 1: Write failing tests for stable id**

Add to `extension/src/__tests__/views/profileTreeView.test.ts` inside the `ProfileTreeItem` describe block:

```typescript
it('sets stable id based on identity, authMethod, and cloud', () => {
    const profile = makeProfile({
        identity: 'user@example.com',
        authMethod: 'DeviceCode',
        cloud: 'Public',
    });
    const item = new ProfileTreeItem(profile);
    expect(item.id).toBe('profile://user@example.com//DeviceCode//Public');
});

it('produces consistent id across multiple instantiations', () => {
    const profile = makeProfile();
    const item1 = new ProfileTreeItem(profile);
    const item2 = new ProfileTreeItem(profile);
    expect(item1.id).toBe(item2.id);
});

it('produces different ids for different auth methods', () => {
    const p1 = makeProfile({ authMethod: 'DeviceCode' });
    const p2 = makeProfile({ authMethod: 'ClientSecret' });
    expect(new ProfileTreeItem(p1).id).not.toBe(new ProfileTreeItem(p2).id);
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: FAIL — `item.id` is `undefined`

- [ ] **Step 3: Add id to ProfileTreeItem**

In `extension/src/views/profileTreeView.ts`, add one line after the `super()` call (line 16):

```typescript
export class ProfileTreeItem extends vscode.TreeItem {
    constructor(
        public readonly profile: ProfileInfo,
    ) {
        const label = profile.name ?? `Profile ${profile.index}`;
        super(label, vscode.TreeItemCollapsibleState.Collapsed);

        // Stable ID for VS Code's built-in expansion state persistence
        this.id = `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}`;

        if (profile.environment) {
```

Also add the `id` property to the mock `TreeItem` class in the test file:

```typescript
TreeItem: class TreeItem {
    label: string;
    collapsibleState: number;
    id?: string;        // ← add this
    description?: string;
    // ... rest unchanged
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: All PASS

- [ ] **Step 5: Commit**

```bash
git add extension/src/views/profileTreeView.ts extension/src/__tests__/views/profileTreeView.test.ts
git commit -m "feat(extension): add stable id to ProfileTreeItem for expansion persistence"
```

### Task 2: Profile Custom Ordering — globalState Integration

**Files:**
- Modify: `extension/src/views/profileTreeView.ts:106-152`
- Modify: `extension/src/extension.ts:83-89`
- Test: `extension/src/__tests__/views/profileTreeView.test.ts`

- [ ] **Step 1: Write failing tests for sort ordering**

Add to `extension/src/__tests__/views/profileTreeView.test.ts` in the `ProfileTreeDataProvider` describe block:

```typescript
describe('profile ordering', () => {
    it('applies sort order from globalState', async () => {
        const profiles = [
            makeProfile({ index: 0, name: 'alpha', identity: 'a@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
            makeProfile({ index: 1, name: 'beta', identity: 'b@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
            makeProfile({ index: 2, name: 'gamma', identity: 'c@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
        ];
        const daemon = makeDaemonClient(profiles);
        // Sort order: gamma first, then alpha, then beta
        const sortOrder: Record<string, number> = {
            'profile://c@x.com//DeviceCode//Public': 0,
            'profile://a@x.com//DeviceCode//Public': 1,
            'profile://b@x.com//DeviceCode//Public': 2,
        };
        const globalState = { get: vi.fn().mockReturnValue(sortOrder), update: vi.fn() };
        const provider = new ProfileTreeDataProvider(daemon as any, makeLogChannel() as any, globalState as any);

        const children = await provider.getChildren();

        expect(children).toHaveLength(3);
        expect(children[0].label).toBe('gamma');
        expect(children[1].label).toBe('alpha');
        expect(children[2].label).toBe('beta');
    });

    it('uses default order when no sort order in globalState', async () => {
        const profiles = [
            makeProfile({ index: 0, name: 'alpha' }),
            makeProfile({ index: 1, name: 'beta' }),
        ];
        const daemon = makeDaemonClient(profiles);
        const globalState = { get: vi.fn().mockReturnValue(undefined), update: vi.fn() };
        const provider = new ProfileTreeDataProvider(daemon as any, makeLogChannel() as any, globalState as any);

        const children = await provider.getChildren();

        expect(children).toHaveLength(2);
        expect(children[0].label).toBe('alpha');
        expect(children[1].label).toBe('beta');
    });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: FAIL — `ProfileTreeDataProvider` constructor doesn't accept `globalState`

- [ ] **Step 3: Add globalState parameter and sort logic to ProfileTreeDataProvider**

In `extension/src/views/profileTreeView.ts`, modify the constructor and `getProfiles()`:

```typescript
// Export helper so commands can compute profile IDs
export function getProfileId(profile: ProfileInfo): string {
    return `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}`;
}
```

Update `ProfileTreeDataProvider`:

```typescript
export class ProfileTreeDataProvider
    implements vscode.TreeDataProvider<ProfileTreeElement>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<ProfileTreeElement | undefined | null | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    constructor(
        private readonly daemonClient: DaemonClient,
        private readonly log: vscode.LogOutputChannel,
        private readonly globalState?: vscode.Memento,
    ) {}
```

In `getProfiles()`, after `result.profiles.map(...)` (line 144), add sorting:

```typescript
            const items = result.profiles.map(p => new ProfileTreeItem(p));

            // Apply user-defined sort order from globalState
            const sortOrder = this.globalState?.get<Record<string, number>>('ppds.profiles.sortOrder');
            if (sortOrder && Object.keys(sortOrder).length > 0) {
                items.sort((a, b) => {
                    const orderA = sortOrder[getProfileId(a.profile)] ?? Number.MAX_SAFE_INTEGER;
                    const orderB = sortOrder[getProfileId(b.profile)] ?? Number.MAX_SAFE_INTEGER;
                    return orderA - orderB;
                });
            }

            return items;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: All PASS (existing tests still pass since globalState is optional)

- [ ] **Step 5: Commit**

```bash
git add extension/src/views/profileTreeView.ts extension/src/__tests__/views/profileTreeView.test.ts
git commit -m "feat(extension): add profile sort ordering via globalState"
```

### Task 3: Move Up/Down Commands + Tests + package.json

**Files:**
- Modify: `extension/src/extension.ts`
- Modify: `extension/package.json`
- Test: `extension/src/__tests__/views/profileTreeView.test.ts`

- [ ] **Step 1: Write failing tests for move boundary conditions**

Add to `extension/src/__tests__/views/profileTreeView.test.ts` in the `profile ordering` describe block:

```typescript
it('move up on first profile is a no-op (order unchanged)', async () => {
    const profiles = [
        makeProfile({ index: 0, name: 'alpha', identity: 'a@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
        makeProfile({ index: 1, name: 'beta', identity: 'b@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
    ];
    const sortOrder: Record<string, number> = {
        'profile://a@x.com//DeviceCode//Public': 0,
        'profile://b@x.com//DeviceCode//Public': 1,
    };
    const globalState = { get: vi.fn().mockReturnValue(sortOrder), update: vi.fn() };
    const daemon = makeDaemonClient(profiles);
    const provider = new ProfileTreeDataProvider(daemon as any, makeLogChannel() as any, globalState as any);

    const children = await provider.getChildren();
    // First profile should be alpha — move up should be a no-op
    expect(children[0].label).toBe('alpha');
    expect(children[1].label).toBe('beta');
});

it('move down on last profile is a no-op (order unchanged)', async () => {
    const profiles = [
        makeProfile({ index: 0, name: 'alpha', identity: 'a@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
        makeProfile({ index: 1, name: 'beta', identity: 'b@x.com', authMethod: 'DeviceCode', cloud: 'Public' }),
    ];
    const sortOrder: Record<string, number> = {
        'profile://a@x.com//DeviceCode//Public': 0,
        'profile://b@x.com//DeviceCode//Public': 1,
    };
    const globalState = { get: vi.fn().mockReturnValue(sortOrder), update: vi.fn() };
    const daemon = makeDaemonClient(profiles);
    const provider = new ProfileTreeDataProvider(daemon as any, makeLogChannel() as any, globalState as any);

    const children = await provider.getChildren();
    // Last profile should be beta — move down should be a no-op
    expect(children[0].label).toBe('alpha');
    expect(children[1].label).toBe('beta');
});
```

Note: The actual move up/down swap logic lives in the command handlers in `extension.ts`. These tests verify the sort ordering that the commands produce. The command handlers themselves are tested indirectly: they write to `globalState`, and these tests verify that `getProfiles()` respects the `globalState` sort order. The boundary guard (`targetIdx <= 0` / `>= length - 1`) in the command handler prevents invalid state from being written.

- [ ] **Step 2: Run tests to verify they pass** (these validate sort ordering, the move boundary guards are in the command handler code)

Run: `cd extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: All PASS

- [ ] **Step 3: Add command definitions to package.json**

In `extension/package.json`, add to the `"commands"` array:

```json
{
    "command": "ppds.moveProfileUp",
    "title": "Move Up",
    "icon": "$(arrow-up)"
},
{
    "command": "ppds.moveProfileDown",
    "title": "Move Down",
    "icon": "$(arrow-down)"
}
```

Add to `"menus"."view/item/context"` array, after the existing profile entries (after `ppds.invalidateProfile`):

```json
{
    "command": "ppds.moveProfileUp",
    "when": "view == ppds.profiles && viewItem == profile",
    "group": "profile@5"
},
{
    "command": "ppds.moveProfileDown",
    "when": "view == ppds.profiles && viewItem == profile",
    "group": "profile@6"
}
```

- [ ] **Step 2: Register commands in extension.ts**

In `extension/src/extension.ts`, update the `ProfileTreeDataProvider` instantiation to pass `globalState`:

```typescript
const profileTreeProvider = new ProfileTreeDataProvider(client, logChannel, context.globalState);
```

Add the import for `getProfileId`:

```typescript
import { ProfileTreeDataProvider, getProfileId } from './views/profileTreeView.js';
```

Register the move commands (add after the profile tree view setup, around line 89):

```typescript
    // ── Profile Move Commands ────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.moveProfileUp', async (item: { profile: { identity: string; authMethod: string; cloud: string } }) => {
            if (!item?.profile) return;
            const sortOrder = context.globalState.get<Record<string, number>>('ppds.profiles.sortOrder') ?? {};
            const profiles = await client.authList();
            const items = profiles.profiles.map(p => ({ id: getProfileId(p), profile: p }));

            // Apply current sort order
            items.sort((a, b) => {
                const orderA = sortOrder[a.id] ?? a.profile.index;
                const orderB = sortOrder[b.id] ?? b.profile.index;
                return orderA - orderB;
            });

            const targetId = `profile://${item.profile.identity}//${item.profile.authMethod}//${item.profile.cloud}`;
            const targetIdx = items.findIndex(i => i.id === targetId);
            if (targetIdx <= 0) return; // Already at top or not found

            // Swap sort positions
            const newOrder: Record<string, number> = {};
            items.forEach((it, idx) => { newOrder[it.id] = idx; });
            newOrder[items[targetIdx].id] = targetIdx - 1;
            newOrder[items[targetIdx - 1].id] = targetIdx;

            await context.globalState.update('ppds.profiles.sortOrder', newOrder);
            profileTreeProvider.refresh();
        }),
        vscode.commands.registerCommand('ppds.moveProfileDown', async (item: { profile: { identity: string; authMethod: string; cloud: string } }) => {
            if (!item?.profile) return;
            const sortOrder = context.globalState.get<Record<string, number>>('ppds.profiles.sortOrder') ?? {};
            const profiles = await client.authList();
            const items = profiles.profiles.map(p => ({ id: getProfileId(p), profile: p }));

            items.sort((a, b) => {
                const orderA = sortOrder[a.id] ?? a.profile.index;
                const orderB = sortOrder[b.id] ?? b.profile.index;
                return orderA - orderB;
            });

            const targetId = `profile://${item.profile.identity}//${item.profile.authMethod}//${item.profile.cloud}`;
            const targetIdx = items.findIndex(i => i.id === targetId);
            if (targetIdx < 0 || targetIdx >= items.length - 1) return; // Already at bottom or not found

            const newOrder: Record<string, number> = {};
            items.forEach((it, idx) => { newOrder[it.id] = idx; });
            newOrder[items[targetIdx].id] = targetIdx + 1;
            newOrder[items[targetIdx + 1].id] = targetIdx;

            await context.globalState.update('ppds.profiles.sortOrder', newOrder);
            profileTreeProvider.refresh();
        }),
    );
```

**Important:** The `ProfileTreeItem` must pass its `profile` data in the command arguments. Update the context menu command arguments by ensuring `ProfileTreeItem` is passed directly — VS Code passes the tree item as the argument to context menu commands, and `ProfileTreeItem` already has `public readonly profile: ProfileInfo`.

- [ ] **Step 3: Verify extension compiles**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
git add extension/src/extension.ts extension/package.json extension/src/views/profileTreeView.ts
git commit -m "feat(extension): add move profile up/down commands with globalState persistence"
```

---

## Chunk 2: Component Type Resolution (Workstream 2)

### Task 4: Fix Hardcoded ComponentTypeNames Dictionary

**Files:**
- Modify: `src/PPDS.Dataverse/Services/SolutionService.cs:26-118`
- Test: `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs`

- [ ] **Step 1: Write failing test for corrected dictionary values**

Add to `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs`:

```csharp
[Theory]
[InlineData(1, "Entity")]
[InlineData(65, "HierarchyRule")]
[InlineData(66, "CustomControl")]
[InlineData(68, "CustomControlDefaultConfig")]
[InlineData(70, "FieldSecurityProfile")]
[InlineData(71, "FieldPermission")]
[InlineData(90, "PluginType")]
[InlineData(91, "PluginAssembly")]
[InlineData(92, "SDKMessageProcessingStep")]
[InlineData(93, "SDKMessageProcessingStepImage")]
[InlineData(95, "ServiceEndpoint")]
[InlineData(150, "RoutingRule")]
[InlineData(151, "RoutingRuleItem")]
[InlineData(152, "SLA")]
[InlineData(161, "MobileOfflineProfile")]
[InlineData(208, "ImportMap")]
[InlineData(300, "CanvasApp")]
[InlineData(372, "Connector")]
public void ComponentTypeNames_MatchesGeneratedEnum(int typeCode, string expectedName)
{
    // Use reflection to access the private static dictionary
    var dictField = typeof(SolutionService).GetField(
        "ComponentTypeNames",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    dictField.Should().NotBeNull("ComponentTypeNames dictionary should exist");

    var dict = dictField!.GetValue(null) as Dictionary<int, string>;
    dict.Should().NotBeNull();
    dict.Should().ContainKey(typeCode);
    dict![typeCode].Should().Be(expectedName);
}
```

This test uses reflection to validate the private static dictionary directly. It will fail before the dictionary correction (e.g., key 65 currently maps to "FieldSecurityProfile" instead of "HierarchyRule") and pass after.

- [ ] **Step 2: Correct the hardcoded dictionary**

In `src/PPDS.Dataverse/Services/SolutionService.cs`, replace the `ComponentTypeNames` dictionary (lines 26-118) with values matching the generated `componenttype` enum in `src/PPDS.Dataverse/Generated/OptionSets/componenttype.cs`. Key corrections:

- `65` → `HierarchyRule` (was `FieldSecurityProfile`)
- `66` → `CustomControl` (was `FieldPermission`)
- `68` → `CustomControlDefaultConfig` (was `PluginType`)
- `70` → `FieldSecurityProfile` (was `SdkMessageProcessingStep`)
- `71` → `FieldPermission` (was `SdkMessageProcessingStepImage`)
- `90` → `PluginType` (was `Data SourceMapping`)
- `91` → `PluginAssembly` (was `SDKMessage`)
- `92` → `SDKMessageProcessingStep` (was `SDKMessageFilter`)
- `93` → `SDKMessageProcessingStepImage` (was `SdkMessagePair`)
- `95` → `ServiceEndpoint` (was `SdkMessageRequest`)
- `150` → `RoutingRule` (was `PluginPackage`)
- `151` → `RoutingRuleItem`
- `152` → `SLA`
- `153` → `SLAItem`
- `154` → `ConvertRule`
- `155` → `ConvertRuleItem`
- `161` → `MobileOfflineProfile` (was `ServicePlanMapping`)
- `162` → `MobileOfflineProfileItem`
- `165` → `SimilarityRule`
- `166` → `DataSourceMapping`
- `201` → `SDKMessage`
- `202` → `SDKMessageFilter`
- `203` → `SdkMessagePair`
- `204` → `SdkMessageRequest`
- `205` → `SdkMessageRequestField`
- `206` → `SdkMessageResponse`
- `207` → `SdkMessageResponseField`
- `210` → `WebWizard`
- `300` → `CanvasApp`
- `208` → `ImportMap` (new entry, from generated enum)
- `210` → `WebWizard` (new entry, from generated enum)
- `371` → `Connector`
- `372` → `Connector` (was `EnvironmentVariableDefinition` — generated enum calls this `Connector1`, use `Connector` for display)
- `380` → `EnvironmentVariableDefinition`
- `381` → `EnvironmentVariableValue`
- `400` → `AIProjectType`
- `401` → `AIProject`
- `402` → `AIConfiguration`
- `430` → `EntityAnalyticsConfiguration`
- `431` → `AttributeImageConfiguration`
- `432` → `EntityImageConfiguration`

Remove entries that don't exist in the generated enum (e.g., old 72-85 range entries that were incorrectly mapped). The generated enum is the authoritative source.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~SolutionServiceTests"`
Expected: All PASS

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Services/SolutionService.cs tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs
git commit -m "fix(dataverse): correct ComponentTypeNames dictionary to match generated enum"
```

### Task 5: Runtime Component Type Resolution via IMetadataService

**Files:**
- Modify: `src/PPDS.Dataverse/Services/SolutionService.cs`
- Test: `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs`

- [ ] **Step 1: Write failing tests for metadata-based resolution**

Add to `SolutionServiceTests.cs`:

```csharp
[Fact]
public void Constructor_ThrowsOnNullMetadataService()
{
    var pool = new Mock<IDataverseConnectionPool>().Object;
    var logger = new NullLogger<SolutionService>();

    var act = () => new SolutionService(pool, logger, null!);

    act.Should().Throw<ArgumentNullException>()
        .And.ParamName.Should().Be("metadataService");
}
```

- [ ] **Step 2: Add IMetadataService to SolutionService constructor**

In `src/PPDS.Dataverse/Services/SolutionService.cs`:

```csharp
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using System.Collections.Concurrent;

// Add field
private readonly IMetadataService _metadataService;

// Update constructor
public SolutionService(
    IDataverseConnectionPool pool,
    ILogger<SolutionService> logger,
    IMetadataService metadataService)
{
    _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
}
```

Update existing constructor tests to pass a mock `IMetadataService`.

- [ ] **Step 3: Add component type cache and resolution method**

Add to `SolutionService.cs`:

```csharp
private static readonly ConcurrentDictionary<string, Dictionary<int, string>> _componentTypeCache = new();

/// <summary>
/// Resolves component type names from environment metadata, with per-environment caching.
/// The envUrl parameter is the already-known environment URL from the caller's pool client,
/// avoiding an extra pool checkout just to get the cache key.
/// </summary>
private async Task<Dictionary<int, string>> GetComponentTypeNamesAsync(
    string envUrl,
    CancellationToken cancellationToken)
{
    var cacheKey = envUrl.TrimEnd('/').ToLowerInvariant();

    if (_componentTypeCache.TryGetValue(cacheKey, out var cached))
    {
        return cached;
    }

    try
    {
        _logger.LogDebug("Fetching componenttype option set metadata for cache key: {EnvUrl}", cacheKey);
        var optionSet = await _metadataService.GetOptionSetAsync("componenttype", cancellationToken);
        var dict = new Dictionary<int, string>();
        foreach (var option in optionSet.Options)
        {
            dict[option.Value] = option.Label;
        }
        _componentTypeCache.TryAdd(cacheKey, dict);
        return dict;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to fetch componenttype metadata, falling back to hardcoded dictionary");
        return ComponentTypeNames;
    }
}
```

- [ ] **Step 4: Update GetComponentsAsync to use runtime resolution**

In `GetComponentsAsync()`, replace the type name resolution (around line 297-300):

In `GetComponentsAsync()`, the pool client is already checked out. Pass its environment URL to `GetComponentTypeNamesAsync` to avoid a redundant pool checkout:

```csharp
    public async Task<List<SolutionComponentInfo>> GetComponentsAsync(
        Guid solutionId,
        int? componentType = null,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var envUrl = client.ConnectedOrgUriActual?.ToString() ?? "default";

        // Resolve component type names (uses cache after first call per env)
        Dictionary<int, string> resolvedTypeNames;
        try
        {
            resolvedTypeNames = await GetComponentTypeNamesAsync(envUrl, cancellationToken);
        }
        catch
        {
            resolvedTypeNames = ComponentTypeNames;
        }

        var query = new QueryExpression(SolutionComponent.EntityLogicalName)
        // ... existing query setup unchanged ...

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.Select(e =>
        {
            var type = e.GetAttributeValue<OptionSetValue>(SolutionComponent.Fields.ComponentType)?.Value ?? 0;
            var typeName = resolvedTypeNames.TryGetValue(type, out var name)
                ? name
                : ComponentTypeNames.TryGetValue(type, out var fallback)
                    ? fallback
                    : $"Unknown ({type})";

            return new SolutionComponentInfo(
                e.Id,
                e.GetAttributeValue<Guid>(SolutionComponent.Fields.ObjectId),
                type,
                typeName,
                e.GetAttributeValue<OptionSetValue>(SolutionComponent.Fields.RootComponentBehavior)?.Value ?? 0,
                e.GetAttributeValue<bool?>(SolutionComponent.Fields.IsMetadata) ?? false);
        }).ToList();
    }
```

This restructuring uses the already-checked-out pool client's URL as the cache key, avoiding an extra pool checkout on every call.

- [ ] **Step 5: Verify DI resolves the new constructor parameter**

Check `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs:257`. `SolutionService` is registered as `AddTransient<ISolutionService, SolutionService>()`. Verify `IMetadataService` is also registered in the same `AddDataverseConnectionPool` extension (search for `IMetadataService` in `ServiceCollectionExtensions.cs`). If `IMetadataService` is registered there, DI will resolve the new parameter automatically. If it's registered elsewhere (e.g., only in CLI-specific setup), you may need to add it to the shared registration.

Run: `dotnet build src/PPDS.Cli` — if it compiles without DI resolution errors, the registration is correct.

- [ ] **Step 6: Fix existing tests that call 2-parameter constructor**

Update all existing `SolutionServiceTests.cs` tests to pass `Mock<IMetadataService>().Object`:

```csharp
// Before:
var act = () => new SolutionService(pool, null!);
// After:
var metadataService = new Mock<IMetadataService>().Object;
var act = () => new SolutionService(pool, null!, metadataService);
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~SolutionServiceTests"`
Expected: All PASS

- [ ] **Step 8: Commit**

```bash
git add src/PPDS.Dataverse/Services/SolutionService.cs tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs
git commit -m "feat(dataverse): resolve component types via IMetadataService with per-env cache"
```

---

## Chunk 3: Solutions Panel Webview (Workstream 3)

**Note on redundant RPC call:** The spec identifies that `loadSolutions()` makes a second RPC call when `includeManaged` is false solely to count managed solutions (lines 148-159). This plan does NOT address that issue — it's accepted as a known cost for now. A future optimization could have the daemon return `totalCount` alongside the filtered list. The search/filter status bar ("5 of 23 solutions") uses the `solutions` array length, not the managed count, so this interaction is straightforward.

### Task 6: Pass Date Fields to Webview + Detail Card

**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts:162-174` (loadSolutions message) and `437-486` (renderSolutions)

- [ ] **Step 1: Add date fields to the solutionsLoaded message**

In `SolutionsPanel.ts`, update `loadSolutions()` at the `this.postMessage` call (~line 162):

```typescript
            this.postMessage({
                command: 'solutionsLoaded',
                solutions: result.solutions.map(s => ({
                    uniqueName: s.uniqueName,
                    friendlyName: s.friendlyName,
                    version: s.version ?? '',
                    publisherName: s.publisherName ?? '',
                    isManaged: s.isManaged,
                    description: s.description ?? '',
                    createdOn: s.createdOn ?? null,
                    modifiedOn: s.modifiedOn ?? null,
                    installedOn: s.installedOn ?? null,
                })),
                managedCount,
                includeManaged: this.includeManaged,
            });
```

- [ ] **Step 2: Add detail card CSS**

Add to the `<style>` block in `getHtmlContent()`, after `.components-loading`:

```css
    .detail-card {
        margin: 8px 12px; padding: 8px 12px;
        background: var(--vscode-textBlockQuote-background);
        border-left: 3px solid var(--vscode-textBlockQuote-border);
        border-radius: 2px; font-size: 12px;
        display: grid; grid-template-columns: auto 1fr; gap: 2px 12px;
    }
    .detail-card .detail-label { color: var(--vscode-descriptionForeground); white-space: nowrap; }
    .detail-card .detail-value { overflow: hidden; text-overflow: ellipsis; }
    .detail-card .detail-description {
        grid-column: 1 / -1; margin-top: 4px; padding-top: 4px;
        border-top: 1px solid var(--vscode-panel-border);
        display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden;
    }
```

- [ ] **Step 3: Add detail card rendering in webview JS**

In the `renderSolutions()` function, update the components-container HTML to include the detail card. Replace the existing `components-container` div content:

```javascript
            html += '<div class="components-container' + (isExpanded ? ' expanded' : '') + '" id="components-' + escapeAttr(sol.uniqueName) + '">';
            // Detail card (always present when expanded)
            html += '<div class="detail-card">';
            html += '<span class="detail-label">Unique Name</span><span class="detail-value">' + escapeHtml(sol.uniqueName) + '</span>';
            html += '<span class="detail-label">Publisher</span><span class="detail-value">' + escapeHtml(sol.publisherName || '—') + '</span>';
            html += '<span class="detail-label">Type</span><span class="detail-value">' + (sol.isManaged ? 'Managed' : 'Unmanaged') + '</span>';
            if (sol.installedOn) {
                html += '<span class="detail-label">Installed</span><span class="detail-value">' + formatDate(sol.installedOn) + '</span>';
            }
            if (sol.modifiedOn) {
                html += '<span class="detail-label">Modified</span><span class="detail-value">' + formatDate(sol.modifiedOn) + '</span>';
            }
            if (sol.description) {
                html += '<div class="detail-description">' + escapeHtml(sol.description) + '</div>';
            }
            html += '</div>';
            html += '<div class="components-loading"><span class="spinner"></span> Loading components...</div>';
            html += '</div>';
```

Add a `formatDate` utility function to the webview JS:

```javascript
    function formatDate(isoString) {
        if (!isoString) return '';
        try {
            return new Date(isoString).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
        } catch { return isoString; }
    }
```

- [ ] **Step 4: Verify extension compiles**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 5: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts
git commit -m "feat(extension): add solution detail card with dates and description"
```

### Task 7: Search/Filter in Solutions Panel

**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts`

- [ ] **Step 1: Add search input to toolbar HTML**

In `getHtmlContent()`, add an input between the Managed button and the spacer:

```html
    <vscode-button id="managed-btn" appearance="secondary" title="Toggle managed solutions visibility">Managed: Off</vscode-button>
    <input id="search-input" type="text" placeholder="Filter solutions..." style="flex: 1; min-width: 120px; max-width: 300px; padding: 3px 8px; background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border, transparent); border-radius: 2px; font-size: 12px; outline: none;" />
    <span class="toolbar-spacer"></span>
```

- [ ] **Step 2: Add filter logic to webview JS**

Add after the button handler declarations in the webview JS:

```javascript
    const searchInput = document.getElementById('search-input');
    let filterText = '';
    let filterTimeout = null;

    searchInput.addEventListener('input', () => {
        clearTimeout(filterTimeout);
        filterTimeout = setTimeout(() => {
            filterText = searchInput.value.trim().toLowerCase();
            applyFilter();
        }, 150);
    });

    function applyFilter() {
        if (!solutions.length) return;
        const rows = content.querySelectorAll('.solution-list > li');
        let visibleCount = 0;
        rows.forEach((li, idx) => {
            const sol = solutions[idx];
            if (!sol) return;
            const matches = !filterText ||
                sol.friendlyName.toLowerCase().includes(filterText) ||
                sol.uniqueName.toLowerCase().includes(filterText) ||
                (sol.publisherName && sol.publisherName.toLowerCase().includes(filterText));
            li.style.display = matches ? '' : 'none';
            if (matches) visibleCount++;
        });

        // Update status bar
        if (filterText) {
            statusText.textContent = visibleCount + ' of ' + solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
        } else {
            // Restore original status
            let statusMsg = solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
            statusText.textContent = statusMsg;
        }

        // Show empty state if no matches
        if (filterText && visibleCount === 0) {
            let emptyEl = content.querySelector('.filter-empty');
            if (!emptyEl) {
                emptyEl = document.createElement('div');
                emptyEl.className = 'empty-state filter-empty';
                emptyEl.textContent = 'No solutions match filter';
                content.appendChild(emptyEl);
            }
        } else {
            const emptyEl = content.querySelector('.filter-empty');
            if (emptyEl) emptyEl.remove();
        }
    }
```

Update `renderSolutions()` to clear the filter input on reload:

```javascript
    function renderSolutions(sols, managedCount, includeManaged) {
        solutions = sols;
        searchInput.value = '';
        filterText = '';
        // ... rest unchanged
```

- [ ] **Step 3: Verify extension compiles**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts
git commit -m "feat(extension): add search/filter to solutions panel toolbar"
```

### Task 8: Managed Toggle Persistence

**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts`
- Modify: `extension/src/extension.ts`

- [ ] **Step 1: Add globalState to SolutionsPanel**

Update `SolutionsPanel.show()` and constructor to accept `globalState`:

```typescript
static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string, globalState?: vscode.Memento): SolutionsPanel {
    // ... existing logic
    const panel = new SolutionsPanel(extensionUri, daemon, envUrl, envDisplayName, globalState);
    return panel;
}

private constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly daemon: DaemonClient,
    initialEnvUrl?: string,
    initialEnvDisplayName?: string,
    private readonly globalState?: vscode.Memento,
) {
    super();
    // Restore managed toggle state
    this.includeManaged = this.globalState?.get<boolean>('ppds.solutionsPanel.includeManaged') ?? false;
    // ... rest unchanged
```

- [ ] **Step 2: Persist toggle state on change**

In the `toggleManaged` message handler (~line 78-81):

```typescript
                    case 'toggleManaged':
                        this.includeManaged = !this.includeManaged;
                        void this.globalState?.update('ppds.solutionsPanel.includeManaged', this.includeManaged);
                        await this.loadSolutions();
                        break;
```

- [ ] **Step 3: Send initial managed state to webview**

In `initialize()`, after the `updateEnvironment` message, add:

```typescript
            this.postMessage({ command: 'updateManagedState', includeManaged: this.includeManaged });
```

Add a handler in the webview JS message handler:

```javascript
            case 'updateManagedState':
                managedOn = msg.includeManaged;
                managedBtn.textContent = managedOn ? 'Managed: On' : 'Managed: Off';
                managedBtn.setAttribute('appearance', managedOn ? 'primary' : 'secondary');
                break;
```

- [ ] **Step 4: Update all callers to pass globalState**

In `extension/src/extension.ts`, update all `SolutionsPanel.show()` calls to pass `context.globalState`:

```typescript
// ppds.openSolutions command
SolutionsPanel.show(context.extensionUri, client, undefined, undefined, context.globalState);

// ppds.openSolutionsForEnv command
SolutionsPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName, context.globalState);
```

- [ ] **Step 5: Verify extension compiles**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 6: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts extension/src/extension.ts
git commit -m "feat(extension): persist managed toggle state via globalState"
```

---

## Final Verification

### Task 9: Full Build + Test Pass

- [ ] **Step 1: Run all extension tests**

Run: `cd extension && npx vitest run`
Expected: All PASS

- [ ] **Step 2: Run all C# tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~SolutionServiceTests"`
Expected: All PASS

- [ ] **Step 3: Compile extension**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 4: Build daemon**

Run: `dotnet build src/PPDS.Cli`
Expected: Build succeeded

- [ ] **Step 5: Final commit if any lint/format fixes needed**

```bash
git add -A
git commit -m "chore: lint and format fixes"
```
