# Web Resources Panel Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Web Resources panel across all 4 surfaces (Daemon RPC, VS Code extension, TUI, MCP), including a FileSystemProvider for in-editor editing with conflict detection, unpublished change detection, and publish coordination.

**Architecture:** This is fully greenfield — every layer must be built from scratch. The `webresource` entity class must be generated, `IWebResourceService` created, then RPC endpoints, VS Code panel, FileSystemProvider, TUI screen, and MCP tools added. All surfaces call the same service methods through their respective infrastructure (Constitution A1, A2).

**Tech Stack:** C# (.NET 8), TypeScript (VS Code extension + webview), Terminal.Gui (TUI), StreamJsonRpc (RPC), ModelContextProtocol (MCP)

**Spec:** `specs/panel-parity.md` — Panel 6 (Web Resources), Acceptance Criteria AC-WR-01 through AC-WR-18

**GitHub Issues:** #584, #593, #351, #360, #589

---

## Design Decisions

### RetrieveUnpublished + PublishXml as extension methods on IPooledClient

`RetrieveUnpublished` is a Dataverse-wide bound function (works on sitemaps, forms, views — not just web resources). Rather than burying it in `IWebResourceService`, we add extension methods on `IPooledClient`:
- `RetrieveUnpublishedAsync(entityName, id, columnSet, ct)` — wraps `RetrieveUnpublishedRequest` from `Microsoft.Crm.Sdk.Messages`
- `PublishXmlAsync(parameterXml, ct)` — wraps `PublishXmlRequest`
- `PublishAllXmlAsync(ct)` — wraps `PublishAllXmlRequest`

Any service can use these. When we add sitemap/form editing, zero duplication.

### Publish coordination at extension method level

`PublishXmlAsync` and `PublishAllXmlAsync` include a per-environment `SemaphoreSlim` to prevent concurrent publish operations (Dataverse can't handle overlapping publishes). This protects all surfaces — VS Code (via daemon), TUI (direct), MCP (direct), CLI (direct). A `ConcurrentDictionary<string, SemaphoreSlim>` keyed by environment URL, with `PpdsException(ErrorCodes.Operation.InProgress)` if a publish is already running.

### SDK-native solution filtering (no OData threshold)

The legacy extension used OData Web API with URL length limits requiring a "small vs large solution" threshold strategy. The new C# service uses `QueryExpression` with `ConditionOperator.In` and a `Guid[]` — no URL length limit. Solution filtering works at any scale with a single code path.

### FileSystemProvider in extension, RPC calls to daemon

The FSP is inherently a VS Code concept. Conflict detection involves VS Code UI (modals, diff views, notifications) which can't live in the daemon. The RPC endpoints stay clean CRUD operations, reusable by TUI/MCP/CLI. The FSP makes RPC calls via `DaemonClient` for all data operations.

### TUI stays read-only

TUI shows web resources in a table with read-only content viewing and publish. No editing, no conflict detection — terminals can't provide a good code editing experience. This is a surface-appropriate difference, not a Constitution A2 violation — the service methods are the single code path, each UI wraps them appropriately.

### Surface-appropriate staleness protection

Rapid solution filter changes can cause out-of-order responses. Webview uses a monotonically increasing request counter to discard stale responses. TUI uses `CancellationTokenSource` swap (cancel previous load, start new) which also saves wasted Dataverse queries. Both prevent stale data, neither requires shared infrastructure.

### Panel and FileSystemProvider as separate phases

The panel (list/filter/search/publish) is verified independently before the FSP (edit/conflict/diff/publish-notification) is layered on top. This isolates the riskiest piece (FSP with 5 content modes) from the foundational panel, enables independent `/ext-verify` checkpoints, and keeps commits focused.

### Web resource type classification

| Type Code | Name | Extension | Editable |
|-----------|------|-----------|----------|
| 1 | HTML | .html | Yes |
| 2 | CSS | .css | Yes |
| 3 | JavaScript | .js | Yes |
| 4 | XML | .xml | Yes |
| 5 | PNG | .png | No |
| 6 | JPG | .jpg | No |
| 7 | GIF | .gif | No |
| 8 | XAP | .xap | No |
| 9 | XSL | .xsl | Yes |
| 10 | ICO | .ico | No |
| 11 | SVG | .svg | Yes |
| 12 | RESX | .resx | Yes |

Text types (1, 2, 3, 4, 9, 11, 12) are editable via FSP. Binary types (5, 6, 7, 8, 10) are viewable in the list but throw `NonEditableWebResourceError` on edit attempt.

---

## File Structure

### Files to modify

| File | Change |
|------|--------|
| `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs:270` | Register `IWebResourceService` |
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:252` | Add `WebResource` error code category |
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:2067` | Add 6 `webResources/*` RPC methods + DTOs |
| `src/PPDS.Extension/src/types.ts` | Add WebResources TypeScript interfaces |
| `src/PPDS.Extension/src/daemonClient.ts:735` | Add 6 `webResources*()` RPC call methods |
| `src/PPDS.Extension/src/panels/webview/shared/message-types.ts:132` | Add WebResources panel message types |
| `src/PPDS.Extension/src/extension.ts:21` | Import + register WebResourcesPanel commands |
| `src/PPDS.Extension/src/views/toolsTreeView.ts:40` | Add Web Resources to sidebar |
| `src/PPDS.Extension/package.json:238` | Add command contributions |
| `src/PPDS.Extension/esbuild.js` | Add web-resources-panel JS + CSS entries |
| `src/PPDS.Cli/Tui/TuiShell.cs:283` | Add Web Resources menu item + navigation method |

### Files to create

| File | Purpose |
|------|---------|
| `src/PPDS.Dataverse/Generated/Entities/webresource.cs` | Early-bound entity class (generated) |
| `src/PPDS.Dataverse/Pooling/PooledClientExtensions.cs` | RetrieveUnpublished, PublishXml, PublishAllXml extension methods |
| `src/PPDS.Dataverse/Services/IWebResourceService.cs` | Service interface + DTOs |
| `src/PPDS.Dataverse/Services/WebResourceService.cs` | Service implementation |
| `src/PPDS.Extension/src/panels/WebResourcesPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/web-resources-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/web-resources-panel.css` | Panel-specific CSS |
| `src/PPDS.Extension/src/providers/WebResourceFileSystemProvider.ts` | FileSystemProvider implementation |
| `src/PPDS.Extension/src/providers/webResourceUri.ts` | URI creation/parsing utilities |
| `src/PPDS.Extension/src/providers/PublishCoordinator.ts` | Extension-side publish state awareness (optional, for instant UI feedback) |
| `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs` | TUI screen |
| `src/PPDS.Mcp/Tools/WebResourcesListTool.cs` | MCP list tool |
| `src/PPDS.Mcp/Tools/WebResourcesGetTool.cs` | MCP get tool |
| `src/PPDS.Mcp/Tools/WebResourcesPublishTool.cs` | MCP publish tool |

---

## Chunk 1: Foundation — Entity + Extension Methods + Service

### Task 1: Generate webresource early-bound entity

**Files:**
- Create: `src/PPDS.Dataverse/Generated/Entities/webresource.cs`

The `webresource` entity class must follow the same auto-generated pattern as `importjob.cs` — inheriting from `Microsoft.Xrm.Sdk.Entity`, implementing `INotifyPropertyChanging`/`INotifyPropertyChanged`, with a nested `Fields` class containing string constants for all attributes.

- [ ] **Step 1: Generate the entity class**

Use the early-bound entity generation tool (PAC ModelBuilder or equivalent) to generate `webresource.cs`. The entity must include at minimum these fields in the `Fields` class:

```csharp
public partial class Fields
{
    public const string WebResourceId = "webresourceid";
    public const string Name = "name";
    public const string DisplayName = "displayname";
    public const string Content = "content";
    public const string WebResourceType = "webresourcetype";
    public const string IsManaged = "ismanaged";
    public const string CreatedBy = "createdby";
    public const string CreatedOn = "createdon";
    public const string ModifiedBy = "modifiedby";
    public const string ModifiedOn = "modifiedon";
    public const string Description = "description";
    public const string SolutionId = "solutionid";
    public const string IsCustomizable = "iscustomizable";
}
```

If generation tooling is unavailable, create a minimal hand-written entity class following `importjob.cs` pattern. Mark it `auto-generated` so it gets replaced when tooling runs.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`
Expected: Build succeeded.

### Task 2: Create PooledClientExtensions

**Files:**
- Create: `src/PPDS.Dataverse/Pooling/PooledClientExtensions.cs`

Extension methods on `IPooledClient` for Dataverse customization operations, reusable across all entity types.

- [ ] **Step 1: Create the extension methods file**

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Extension methods for IPooledClient providing Dataverse customization operations.
/// These are entity-agnostic and can be used by any service (web resources, sitemaps, forms, etc.).
/// </summary>
public static class PooledClientExtensions
{
    // Per-environment publish lock — prevents concurrent PublishXml operations
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublishLocks = new();

    /// <summary>
    /// Retrieves unpublished content for a customization entity using the RetrieveUnpublished bound function.
    /// Works on web resources, sitemaps, forms, views, ribbons, etc.
    /// </summary>
    public static async Task<Entity> RetrieveUnpublishedAsync(
        this IPooledClient client,
        string entityLogicalName,
        Guid id,
        ColumnSet columnSet,
        CancellationToken cancellationToken = default)
    {
        var request = new RetrieveUnpublishedRequest
        {
            Target = new EntityReference(entityLogicalName, id),
            ColumnSet = columnSet
        };

        var response = (RetrieveUnpublishedResponse)await client.ExecuteAsync(request, cancellationToken);
        return response.Entity;
    }

    /// <summary>
    /// Publishes specific customizations via PublishXml.
    /// Includes per-environment concurrency protection — throws PpdsException if a publish is already in progress.
    /// </summary>
    /// <param name="client">The pooled client.</param>
    /// <param name="parameterXml">The PublishXml parameter XML.</param>
    /// <param name="environmentKey">Environment URL or identifier for lock scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task PublishXmlAsync(
        this IPooledClient client,
        string parameterXml,
        string environmentKey,
        CancellationToken cancellationToken = default)
    {
        var semaphore = PublishLocks.GetOrAdd(environmentKey, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new PpdsException(
                ErrorCodes.Operation.InProgress,
                "A publish operation is already in progress for this environment. Please wait for it to complete.");
        }

        try
        {
            var request = new PublishXmlRequest { ParameterXml = parameterXml };
            await client.ExecuteAsync(request, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Publishes all customizations via PublishAllXml.
    /// Shares the per-environment publish lock with PublishXmlAsync.
    /// </summary>
    public static async Task PublishAllXmlAsync(
        this IPooledClient client,
        string environmentKey,
        CancellationToken cancellationToken = default)
    {
        var semaphore = PublishLocks.GetOrAdd(environmentKey, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new PpdsException(
                ErrorCodes.Operation.InProgress,
                "A publish operation is already in progress for this environment. Please wait for it to complete.");
        }

        try
        {
            var request = new PublishAllXmlRequest();
            await client.ExecuteAsync(request, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
```

**Note:** `PpdsException` and `ErrorCodes` will need a using directive. The `environmentKey` parameter is passed by the calling service (which knows its environment URL from the pool configuration or RPC request).

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`
Expected: Build succeeded. May need to add `Microsoft.Crm.Sdk.Messages` NuGet reference if not already present.

### Task 3: Add WebResource error codes

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:252`

- [ ] **Step 1: Add WebResource error code category**

Insert before the closing brace of `ErrorCodes` class (line 253):

```csharp
    /// <summary>
    /// Web resource operation errors.
    /// </summary>
    public static class WebResource
    {
        /// <summary>Web resource not found by ID.</summary>
        public const string NotFound = "WebResource.NotFound";

        /// <summary>Attempted to edit a binary web resource (PNG/JPG/GIF/ICO/XAP).</summary>
        public const string NotEditable = "WebResource.NotEditable";

        /// <summary>Content conflict — server version has changed since last fetch.</summary>
        public const string Conflict = "WebResource.Conflict";

        /// <summary>Publish failed for one or more web resources.</summary>
        public const string PublishFailed = "WebResource.PublishFailed";
    }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`

### Task 4: Create IWebResourceService interface

**Files:**
- Create: `src/PPDS.Dataverse/Services/IWebResourceService.cs`

- [ ] **Step 1: Create the interface and DTOs**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying, updating, and publishing Dataverse web resources.
/// </summary>
public interface IWebResourceService
{
    /// <summary>
    /// Lists web resources, optionally filtered by solution and/or type.
    /// </summary>
    /// <param name="solutionId">Optional solution ID filter. Uses ConditionOperator.In with component IDs.</param>
    /// <param name="textOnly">If true, excludes binary types (PNG/JPG/GIF/XAP/ICO).</param>
    /// <param name="top">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<WebResourceInfo>> ListAsync(
        Guid? solutionId = null,
        bool textOnly = false,
        int top = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a web resource by ID (metadata only, no content).
    /// </summary>
    Task<WebResourceInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the content of a web resource, optionally unpublished.
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="published">If true, gets published content (standard query). If false, uses RetrieveUnpublished.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Base64-decoded string content for text types, null for binary or missing content.</returns>
    Task<WebResourceContent?> GetContentAsync(
        Guid id,
        bool published = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets only the modifiedOn timestamp for conflict detection (lightweight query).
    /// </summary>
    Task<DateTime?> GetModifiedOnAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the content of a web resource. Does NOT publish.
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="content">The text content (will be base64 encoded before sending).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateContentAsync(Guid id, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes specific web resources via PublishXml.
    /// </summary>
    /// <param name="ids">The web resource IDs to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of web resources published.</returns>
    Task<int> PublishAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes all customizations via PublishAllXml.
    /// </summary>
    Task PublishAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Web resource metadata (no content).
/// </summary>
public record WebResourceInfo(
    Guid Id,
    string Name,
    string? DisplayName,
    int WebResourceType,
    bool IsManaged,
    string? CreatedByName,
    DateTime? CreatedOn,
    string? ModifiedByName,
    DateTime? ModifiedOn)
{
    /// <summary>
    /// Human-readable type name derived from type code.
    /// Single code path for all surfaces (Constitution A2).
    /// </summary>
    public string TypeName => WebResourceType switch
    {
        1 => "HTML",
        2 => "CSS",
        3 => "JavaScript",
        4 => "XML",
        5 => "PNG",
        6 => "JPG",
        7 => "GIF",
        8 => "XAP (Silverlight)",
        9 => "XSL",
        10 => "ICO",
        11 => "SVG",
        12 => "RESX",
        _ => $"Unknown ({WebResourceType})"
    };

    /// <summary>
    /// File extension derived from type code (without dot).
    /// </summary>
    public string FileExtension => WebResourceType switch
    {
        1 => "html",
        2 => "css",
        3 => "js",
        4 => "xml",
        5 => "png",
        6 => "jpg",
        7 => "gif",
        8 => "xap",
        9 => "xsl",
        10 => "ico",
        11 => "svg",
        12 => "resx",
        _ => "bin"
    };

    /// <summary>
    /// Whether this type is text-based and editable in an editor.
    /// </summary>
    public bool IsTextType => WebResourceType is 1 or 2 or 3 or 4 or 9 or 11 or 12;
}

/// <summary>
/// Web resource content with metadata needed for editing.
/// </summary>
public record WebResourceContent(
    Guid Id,
    string Name,
    int WebResourceType,
    string? Content,
    DateTime? ModifiedOn);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`

### Task 5: Create WebResourceService implementation

**Files:**
- Create: `src/PPDS.Dataverse/Services/WebResourceService.cs`

- [ ] **Step 1: Create the service implementation**

Follow `ImportJobService.cs` pattern — constructor injection of `IDataverseConnectionPool` and `ILogger<T>`, `QueryExpression` with early-bound entity fields, pool get/use/dispose pattern.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying, updating, and publishing Dataverse web resources.
/// </summary>
public class WebResourceService : IWebResourceService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ISolutionService _solutionService;
    private readonly ILogger<WebResourceService> _logger;

    // Binary type codes that are not editable
    private static readonly HashSet<int> BinaryTypes = [5, 6, 7, 8, 10];

    // Text type codes for the textOnly filter
    private static readonly int[] TextTypes = [1, 2, 3, 4, 9, 11, 12];

    // Standard columns for list queries
    private static readonly string[] ListColumns =
    [
        WebResource.Fields.WebResourceId,
        WebResource.Fields.Name,
        WebResource.Fields.DisplayName,
        WebResource.Fields.WebResourceType,
        WebResource.Fields.IsManaged,
        WebResource.Fields.CreatedBy,
        WebResource.Fields.CreatedOn,
        WebResource.Fields.ModifiedBy,
        WebResource.Fields.ModifiedOn
    ];

    public WebResourceService(
        IDataverseConnectionPool pool,
        ISolutionService solutionService,
        ILogger<WebResourceService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _solutionService = solutionService ?? throw new ArgumentNullException(nameof(solutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<WebResourceInfo>> ListAsync(
        Guid? solutionId = null,
        bool textOnly = false,
        int top = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(ListColumns),
            TopCount = top
        };

        // Text-only filter: exclude binary types
        if (textOnly)
        {
            query.Criteria.AddCondition(
                WebResource.Fields.WebResourceType,
                ConditionOperator.In,
                TextTypes.Cast<object>().ToArray());
        }

        // Solution filter: get component IDs via SolutionService, then IN filter
        if (solutionId.HasValue)
        {
            var components = await _solutionService.GetComponentsAsync(
                solutionId.Value,
                componentType: 61, // WebResource
                cancellationToken: cancellationToken);

            var webResourceIds = components
                .Select(c => c.ObjectId)
                .Where(id => id != Guid.Empty)
                .ToArray();

            if (webResourceIds.Length == 0)
            {
                return []; // No web resources in this solution
            }

            query.Criteria.AddCondition(
                WebResource.Fields.WebResourceId,
                ConditionOperator.In,
                webResourceIds.Cast<object>().ToArray());
        }

        // Default sort: name ascending
        query.AddOrder(WebResource.Fields.Name, OrderType.Ascending);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.Select(MapToWebResourceInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<WebResourceInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(ListColumns),
            TopCount = 1
        };
        query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.FirstOrDefault() is { } entity ? MapToWebResourceInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<WebResourceContent?> GetContentAsync(
        Guid id,
        bool published = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var contentColumns = new ColumnSet(
            WebResource.Fields.Content,
            WebResource.Fields.Name,
            WebResource.Fields.WebResourceType,
            WebResource.Fields.ModifiedOn);

        Entity entity;
        if (published)
        {
            // Standard retrieve returns published content
            var query = new QueryExpression(WebResource.EntityLogicalName)
            {
                ColumnSet = contentColumns,
                TopCount = 1
            };
            query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

            var results = await client.RetrieveMultipleAsync(query, cancellationToken);
            entity = results.Entities.FirstOrDefault()
                ?? throw new PpdsException(ErrorCodes.WebResource.NotFound, $"Web resource '{id}' not found");
        }
        else
        {
            // RetrieveUnpublished returns latest saved (unpublished) content
            entity = await client.RetrieveUnpublishedAsync(
                WebResource.EntityLogicalName, id, contentColumns, cancellationToken);
        }

        var base64Content = entity.GetAttributeValue<string>(WebResource.Fields.Content);
        var decodedContent = base64Content != null
            ? Encoding.UTF8.GetString(Convert.FromBase64String(base64Content))
            : null;

        return new WebResourceContent(
            id,
            entity.GetAttributeValue<string>(WebResource.Fields.Name) ?? "",
            entity.GetAttributeValue<OptionSetValue>(WebResource.Fields.WebResourceType)?.Value ?? 0,
            decodedContent,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn));
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetModifiedOnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(WebResource.Fields.ModifiedOn),
            TopCount = 1
        };
        query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.FirstOrDefault()?.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn);
    }

    /// <inheritdoc />
    public async Task UpdateContentAsync(Guid id, string content, CancellationToken cancellationToken = default)
    {
        // Validate editability
        var info = await GetAsync(id, cancellationToken)
            ?? throw new PpdsException(ErrorCodes.WebResource.NotFound, $"Web resource '{id}' not found");

        if (!info.IsTextType)
        {
            throw new PpdsException(
                ErrorCodes.WebResource.NotEditable,
                $"Web resource '{info.Name}' is a {info.TypeName} file and cannot be edited. Binary types are read-only.");
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var update = new Entity(WebResource.EntityLogicalName, id);
        update[WebResource.Fields.Content] = base64Content;

        await client.UpdateAsync(update, cancellationToken);

        _logger.LogInformation("Updated web resource content: {Name} ({Id})", info.Name, id);
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Build PublishXml parameter XML for web resources
        var webResourceXml = string.Join("",
            ids.Select(id => $"<webresource>{{{id}}}</webresource>"));
        var parameterXml = $"<importexportxml><webresources>{webResourceXml}</webresources></importexportxml>";

        // Get environment key for publish coordination
        var environmentKey = client.ConnectedOrgUniqueName ?? "default";

        await client.PublishXmlAsync(parameterXml, environmentKey, cancellationToken);

        _logger.LogInformation("Published {Count} web resource(s)", ids.Count);
        return ids.Count;
    }

    /// <inheritdoc />
    public async Task PublishAllAsync(CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var environmentKey = client.ConnectedOrgUniqueName ?? "default";
        await client.PublishAllXmlAsync(environmentKey, cancellationToken);

        _logger.LogInformation("Published all customizations");
    }

    private static WebResourceInfo MapToWebResourceInfo(Entity entity)
    {
        var createdByRef = entity.GetAttributeValue<EntityReference>(WebResource.Fields.CreatedBy);
        var modifiedByRef = entity.GetAttributeValue<EntityReference>(WebResource.Fields.ModifiedBy);
        var typeValue = entity.GetAttributeValue<OptionSetValue>(WebResource.Fields.WebResourceType);

        return new WebResourceInfo(
            entity.Id,
            entity.GetAttributeValue<string>(WebResource.Fields.Name) ?? "",
            entity.GetAttributeValue<string>(WebResource.Fields.DisplayName),
            typeValue?.Value ?? 0,
            entity.GetAttributeValue<bool?>(WebResource.Fields.IsManaged) ?? false,
            createdByRef?.Name,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.CreatedOn),
            modifiedByRef?.Name,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn));
    }
}
```

**Note:** `PpdsException` is in `PPDS.Cli.Infrastructure.Errors` — verify the reference is accessible from PPDS.Dataverse. If not, the error wrapping moves to the RPC handler layer instead (caller wraps service exceptions).

- [ ] **Step 2: Register in DI**

In `ServiceCollectionExtensions.cs`, add after the Phase 3 services line (~270):

```csharp
            // Phase 2d services (Web Resources)
            services.AddTransient<IWebResourceService, WebResourceService>();
```

- [ ] **Step 3: Build the full solution to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded. Resolve any missing references.

- [ ] **Step 4: Run existing tests to verify no regressions**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All existing tests pass.

---

## Chunk 2: RPC Endpoints

### Task 6: Add webResources/* RPC methods

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add 6 RPC method handlers**

Insert a new `#region Web Resources` after `#endregion` (Import Jobs region, line 2067):

```csharp
    #region Web Resources

    /// <summary>
    /// Lists web resources for an environment, optionally filtered by solution.
    /// Maps to: IWebResourceService.ListAsync
    /// </summary>
    [JsonRpcMethod("webResources/list")]
    public async Task<WebResourcesListResponse> WebResourcesListAsync(
        string? solutionId = null,
        bool textOnly = true,
        int top = 5000,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        Guid? parsedSolutionId = null;
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            if (!Guid.TryParse(solutionId, out var sid))
                throw new RpcException(ErrorCodes.Validation.InvalidValue, "The 'solutionId' must be a valid GUID");
            parsedSolutionId = sid;
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            var resources = await service.ListAsync(parsedSolutionId, textOnly, top, ct);

            return new WebResourcesListResponse
            {
                Resources = resources.Select(MapWebResourceToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a web resource with content. Uses RetrieveUnpublished by default.
    /// Maps to: IWebResourceService.GetContentAsync
    /// </summary>
    [JsonRpcMethod("webResources/get")]
    public async Task<WebResourcesGetResponse> WebResourcesGetAsync(
        string id,
        bool published = false,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            var content = await service.GetContentAsync(resourceId, published, ct);

            return new WebResourcesGetResponse
            {
                Resource = content != null ? new WebResourceDetailDto
                {
                    Id = content.Id.ToString(),
                    Name = content.Name,
                    WebResourceType = content.WebResourceType,
                    Content = content.Content,
                    ModifiedOn = content.ModifiedOn?.ToString("o")
                } : null
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets only the modifiedOn timestamp for a web resource (lightweight conflict detection).
    /// Maps to: IWebResourceService.GetModifiedOnAsync
    /// </summary>
    [JsonRpcMethod("webResources/getModifiedOn")]
    public async Task<WebResourcesGetModifiedOnResponse> WebResourcesGetModifiedOnAsync(
        string id,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            var modifiedOn = await service.GetModifiedOnAsync(resourceId, ct);

            return new WebResourcesGetModifiedOnResponse
            {
                ModifiedOn = modifiedOn?.ToString("o")
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates web resource content (does not publish).
    /// Maps to: IWebResourceService.UpdateContentAsync
    /// </summary>
    [JsonRpcMethod("webResources/update")]
    public async Task<WebResourcesUpdateResponse> WebResourcesUpdateAsync(
        string id,
        string content,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");
        if (content == null)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'content' parameter is required");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            await service.UpdateContentAsync(resourceId, content, ct);

            return new WebResourcesUpdateResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes specific web resources.
    /// Maps to: IWebResourceService.PublishAsync
    /// </summary>
    [JsonRpcMethod("webResources/publish")]
    public async Task<WebResourcesPublishResponse> WebResourcesPublishAsync(
        string[] ids,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'ids' parameter must contain at least one GUID");

        var parsedIds = ids.Select(id =>
        {
            if (!Guid.TryParse(id, out var guid))
                throw new RpcException(ErrorCodes.Validation.InvalidValue, $"Invalid GUID in ids: '{id}'");
            return guid;
        }).ToList();

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            var count = await service.PublishAsync(parsedIds, ct);

            return new WebResourcesPublishResponse { PublishedCount = count };
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes all customizations.
    /// Maps to: IWebResourceService.PublishAllAsync
    /// </summary>
    [JsonRpcMethod("webResources/publishAll")]
    public async Task<WebResourcesPublishAllResponse> WebResourcesPublishAllAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IWebResourceService>();
            await service.PublishAllAsync(ct);

            return new WebResourcesPublishAllResponse { Success = true };
        }, cancellationToken);
    }

    private static WebResourceInfoDto MapWebResourceToDto(WebResourceInfo wr)
    {
        return new WebResourceInfoDto
        {
            Id = wr.Id.ToString(),
            Name = wr.Name,
            DisplayName = wr.DisplayName,
            Type = wr.WebResourceType,
            TypeName = wr.TypeName,
            FileExtension = wr.FileExtension,
            IsManaged = wr.IsManaged,
            IsTextType = wr.IsTextType,
            CreatedBy = wr.CreatedByName,
            CreatedOn = wr.CreatedOn?.ToString("o"),
            ModifiedBy = wr.ModifiedByName,
            ModifiedOn = wr.ModifiedOn?.ToString("o")
        };
    }

    #endregion
```

- [ ] **Step 2: Add DTO classes**

Add in the `#region Response DTOs` section (after Import Jobs DTOs, before `#endregion`):

```csharp
#region Web Resources DTOs

public class WebResourcesListResponse
{
    [JsonPropertyName("resources")]
    public List<WebResourceInfoDto> Resources { get; set; } = [];
}

public class WebResourcesGetResponse
{
    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebResourceDetailDto? Resource { get; set; }
}

public class WebResourcesGetModifiedOnResponse
{
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class WebResourcesUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class WebResourcesPublishResponse
{
    [JsonPropertyName("publishedCount")]
    public int PublishedCount { get; set; }
}

public class WebResourcesPublishAllResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class WebResourceInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("fileExtension")]
    public string FileExtension { get; set; } = "";

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isTextType")]
    public bool IsTextType { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedBy { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class WebResourceDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("webResourceType")]
    public int WebResourceType { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

#endregion
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build PPDS.sln -v q`

### Task 7: Add TypeScript daemon client methods

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Add web resources RPC methods**

Add after the Import Jobs section (~line 735):

```typescript
    // ── Web Resources ──────────────────────────────────────────────

    async webResourcesList(
        solutionId?: string,
        textOnly = true,
        top = 5000,
        environmentUrl?: string,
    ): Promise<{ resources: WebResourceInfoDto[] }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/list', {
            solutionId,
            textOnly,
            top,
            environmentUrl,
        });
    }

    async webResourcesGet(
        id: string,
        published = false,
        environmentUrl?: string,
    ): Promise<{ resource: WebResourceDetailDto | null }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/get', {
            id,
            published,
            environmentUrl,
        });
    }

    async webResourcesGetModifiedOn(
        id: string,
        environmentUrl?: string,
    ): Promise<{ modifiedOn: string | null }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/getModifiedOn', {
            id,
            environmentUrl,
        });
    }

    async webResourcesUpdate(
        id: string,
        content: string,
        environmentUrl?: string,
    ): Promise<{ success: boolean }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/update', {
            id,
            content,
            environmentUrl,
        });
    }

    async webResourcesPublish(
        ids: string[],
        environmentUrl?: string,
    ): Promise<{ publishedCount: number }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/publish', {
            ids,
            environmentUrl,
        });
    }

    async webResourcesPublishAll(
        environmentUrl?: string,
    ): Promise<{ success: boolean }> {
        await this.ensureConnected();
        return this.connection!.sendRequest('webResources/publishAll', {
            environmentUrl,
        });
    }
```

- [ ] **Step 2: Add TypeScript interfaces**

Add to `src/PPDS.Extension/src/types.ts`:

```typescript
export interface WebResourceInfoDto {
    id: string;
    name: string;
    displayName?: string;
    type: number;
    typeName: string;
    fileExtension: string;
    isManaged: boolean;
    isTextType: boolean;
    createdBy?: string;
    createdOn?: string;
    modifiedBy?: string;
    modifiedOn?: string;
}

export interface WebResourceDetailDto {
    id: string;
    name: string;
    webResourceType: number;
    content?: string;
    modifiedOn?: string;
}
```

- [ ] **Step 3: Build extension to verify**

Run: `npm run ext:build` (from `src/PPDS.Extension/`)

---

## Chunk 3: VS Code Panel + Table

### Task 8: Add Web Resources message types

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add message type definitions**

Append after Import Jobs section:

```typescript
// ── Web Resources Panel ──────────────────────────────────────

export type WebResourcesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' }
    | { command: 'requestSolutionList' }
    | { command: 'selectSolution'; solutionId: string | null }
    | { command: 'toggleTextOnly'; textOnly: boolean }
    | { command: 'search'; query: string }
    | { command: 'openWebResource'; id: string; name: string; isTextType: boolean }
    | { command: 'publishSelected'; ids: string[] }
    | { command: 'publishAll' }
    | { command: 'openInMaker' }
    | { command: 'webviewError'; error: string; stack?: string };

export type WebResourcesPanelHostToWebview =
    | { command: 'updateEnvironment'; url: string; displayName: string; type: string | null; color: string | null; environmentId: string | null; profileName: string | null }
    | { command: 'solutionListLoaded'; solutions: Array<{ id: string; uniqueName: string; friendlyName: string }> }
    | { command: 'webResourcesLoaded'; resources: WebResourceInfoDto[]; requestId: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'publishResult'; count: number }
    | { command: 'daemonReconnected' };

// Re-export for webview-side usage
import type { WebResourceInfoDto } from '../../../types.js';
export type { WebResourceInfoDto };
```

### Task 9: Create WebResourcesPanel host-side

**Files:**
- Create: `src/PPDS.Extension/src/panels/WebResourcesPanel.ts`

- [ ] **Step 1: Create the host-side panel**

Follow `ImportJobsPanel.ts` pattern. Key differences:
- Solution filter (persisted via `globalState`)
- Text-only toggle (default: true)
- Request versioning counter for stale response protection
- Row click dispatches to FileSystemProvider URI (wired in Chunk 4; initially opens in Maker)

The panel should:
1. Extend `WebviewPanelBase<WebResourcesPanelWebviewToHost, WebResourcesPanelHostToWebview>`
2. Handle messages: `ready`, `refresh`, `requestEnvironmentList`, `requestSolutionList`, `selectSolution`, `toggleTextOnly`, `search`, `openWebResource`, `publishSelected`, `publishAll`, `openInMaker`
3. Track `requestId` counter — increment on every load, include in response, webview discards if stale
4. Persist solution filter selection in `context.globalState` keyed by panel view type
5. Load solutions via `daemon.solutionsList()` for solution filter dropdown
6. Load web resources via `daemon.webResourcesList()`

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type { WebResourcesPanelWebviewToHost, WebResourcesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class WebResourcesPanel extends WebviewPanelBase<WebResourcesPanelWebviewToHost, WebResourcesPanelHostToWebview> {
    // ... follow ImportJobsPanel.ts structure with:
    // - static instances array + MAX_PANELS
    // - static show() factory
    // - private constructor creating webview panel
    // - handleMessage() with exhaustive switch
    // - requestId counter for stale response protection
    // - solution filter persistence via globalState
    // - environment picker, solution list, web resources loading
}
```

Implementation should be ~300-400 lines following Import Jobs pattern closely.

- [ ] **Step 2: Register panel commands in extension.ts**

Add import and registration:

```typescript
import { WebResourcesPanel } from './panels/WebResourcesPanel.js';

// In registerPanelCommands():
vscode.commands.registerCommand('ppds.openWebResources', () => {
    WebResourcesPanel.show(context.extensionUri, client);
});
vscode.commands.registerCommand('ppds.openWebResourcesForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
    WebResourcesPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
}));
```

- [ ] **Step 3: Add to tools tree view**

In `toolsTreeView.ts`, add Web Resources item after Import Jobs:

```typescript
new ToolTreeItem('Web Resources', 'ppds.openWebResources', 'file-code', hasProfile),
```

- [ ] **Step 4: Add command contributions to package.json**

Add after Import Jobs commands:

```json
{
    "command": "ppds.openWebResources",
    "title": "Web Resources",
    "category": "PPDS",
    "icon": "$(file-code)"
},
{
    "command": "ppds.openWebResourcesForEnv",
    "title": "Open Web Resources for Environment",
    "category": "PPDS"
}
```

### Task 10: Create webview-side script

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/web-resources-panel.ts`

- [ ] **Step 1: Create the browser-side script**

Follow `import-jobs-panel.ts` pattern with additions:
- Solution filter dropdown (load solutions, persist selection, post `selectSolution` on change)
- Text-only toggle checkbox (default checked, post `toggleTextOnly` on change)
- Search input with debounce (client-side filtering via `FilterBar` or inline)
- DataTable with columns: Name (clickable link for text types), Display Name, Type (with icon/badge), Managed, Created By, Created On, Modified By, Modified On
- Request versioning: store `lastRequestId`, ignore responses with older IDs
- Row click: post `openWebResource` with id, name, isTextType
- Publish selected: toolbar button posts `publishSelected` with selected row IDs
- Type icons/badges for visual differentiation (JS, CSS, HTML, XML, SVG, etc.)

- [ ] **Step 2: Add esbuild entries**

In `esbuild.js`, add JS and CSS entries for web-resources-panel.

### Task 11: Create panel styles

**Files:**
- Create: `src/PPDS.Extension/src/panels/styles/web-resources-panel.css`

- [ ] **Step 1: Create panel CSS**

Import shared.css, add:
- Type badges (color-coded by web resource type)
- Solution filter dropdown styling
- Text-only toggle styling
- Search input styling
- Clickable name column styling (underline on hover for text types)
- Managed badge styling
- DataTable column widths appropriate for web resource data

- [ ] **Step 2: Build and verify**

Run: `npm run ext:build`

- [ ] **Step 3: Run extension type checks**

Run: `npm run ext:typecheck`

---

## Chunk 4: FileSystemProvider

### Task 12: Create URI utilities

**Files:**
- Create: `src/PPDS.Extension/src/providers/webResourceUri.ts`

- [ ] **Step 1: Create URI creation and parsing**

```typescript
import * as vscode from 'vscode';

export const WEB_RESOURCE_SCHEME = 'ppds-webresource';

export type WebResourceContentMode =
    | 'unpublished'   // Default — latest saved (editable)
    | 'published'     // Currently published (read-only, for diff)
    | 'server-current'  // Fresh fetch bypassing cache (for conflict diff)
    | 'local-pending';  // Stored pending save content (for conflict diff)

export interface ParsedWebResourceUri {
    environmentId: string;
    webResourceId: string;
    filename: string;
    mode: WebResourceContentMode;
}

/**
 * Creates a ppds-webresource:/// URI.
 * Format: ppds-webresource:///environmentId/webResourceId/filename.ext[?mode=...]
 */
export function createWebResourceUri(
    environmentId: string,
    webResourceId: string,
    filename: string,
    mode: WebResourceContentMode = 'unpublished',
): vscode.Uri {
    const path = `/${environmentId}/${webResourceId}/${filename}`;
    const query = mode !== 'unpublished' ? `mode=${mode}` : '';
    return vscode.Uri.from({ scheme: WEB_RESOURCE_SCHEME, path, query });
}

/**
 * Parses a ppds-webresource URI back to its components.
 */
export function parseWebResourceUri(uri: vscode.Uri): ParsedWebResourceUri {
    const parts = uri.path.split('/').filter(Boolean);
    if (parts.length < 3) {
        throw new Error(`Invalid web resource URI: ${uri.toString()}`);
    }

    const params = new URLSearchParams(uri.query);
    const mode = (params.get('mode') as WebResourceContentMode) ?? 'unpublished';

    return {
        environmentId: parts[0],
        webResourceId: parts[1],
        filename: parts.slice(2).join('/'),
        mode,
    };
}

/**
 * Maps web resource type code to VS Code language ID for auto-detection.
 */
export function getLanguageId(webResourceType: number): string | undefined {
    switch (webResourceType) {
        case 1: return 'html';
        case 2: return 'css';
        case 3: return 'javascript';
        case 4: return 'xml';
        case 9: return 'xsl';
        case 11: return 'xml'; // SVG is XML
        case 12: return 'xml'; // RESX is XML
        default: return undefined;
    }
}
```

### Task 13: Create WebResourceFileSystemProvider

**Files:**
- Create: `src/PPDS.Extension/src/providers/WebResourceFileSystemProvider.ts`

- [ ] **Step 1: Create the FileSystemProvider**

This is the most complex file. It implements `vscode.FileSystemProvider` and handles:

**Internal state maps** (keyed by `envId:resourceId`):
- `serverState`: Map of `{ modifiedOn: string; lastKnownContent: Uint8Array }` for conflict detection
- `pendingFetches`: Map of `Promise<Uint8Array>` for deduplicating concurrent reads
- `preFetchedContent`: Map of `Uint8Array` for instant delivery on open
- `pendingSaveContent`: Map of `Uint8Array` for conflict diff display

**Event emitters:**
- `onDidChangeFile` — standard FSP event
- `onDidSaveWebResource` — custom event for panel auto-refresh

**readFile routing by content mode:**
- `unpublished` (default): Fetch via `daemon.webResourcesGet(id, false)`, decode to Uint8Array, update serverState cache, set language mode
- `published`: Fetch via `daemon.webResourcesGet(id, true)`, read-only
- `server-current`: Fresh fetch bypassing cache (for conflict diff left pane)
- `local-pending`: Return stored pendingSaveContent (for conflict diff right pane)

**writeFile flow:**
1. Validate URI, get parsed components
2. Skip if content unchanged (byte comparison vs `serverState.lastKnownContent`)
3. Conflict detection: fetch current modifiedOn via `daemon.webResourcesGetModifiedOn(id)`, compare with cached `serverState.modifiedOn`
4. If conflict: show modal ("Compare First" / "Overwrite" / "Discard My Work")
   - "Compare First": store content in `pendingSaveContent`, open diff (`server-current` left, `local-pending` right), show resolution modal ("Save My Version" / "Use Server Version" / "Cancel"), clean up `pendingSaveContent` in finally
   - "Overwrite": proceed to save
   - "Discard": reload from server
5. Call `daemon.webResourcesUpdate(id, textContent)`
6. Refresh cache: fetch new modifiedOn, update serverState
7. Fire `onDidChangeFile` and `onDidSaveWebResource` events
8. Show non-modal notification "Saved: filename" with "Publish" action button
9. If user clicks Publish: call `daemon.webResourcesPublish([id])`

**On open flow (triggered by panel row click):**
1. Fetch published + unpublished content in parallel
2. If they differ: show quick pick ("Edit Unpublished" / "Edit Published" / "Cancel")
3. Open chosen version URI; set language mode via `vscode.languages.setTextDocumentLanguage()`

**Binary protection:**
- `stat()` returns readonly flag for binary types
- `writeFile()` throws `vscode.FileSystemError.NoPermissions` for binary types

**Supported operations:**
- `readFile`, `writeFile`, `stat` — fully implemented
- `readDirectory` — returns empty array
- `delete`, `rename`, `createDirectory` — throw `NoPermissions`

Implementation will be ~400-500 lines.

- [ ] **Step 2: Register FSP in extension.ts**

```typescript
import { WebResourceFileSystemProvider, WEB_RESOURCE_SCHEME } from './providers/WebResourceFileSystemProvider.js';

// In activate():
const webResourceFsp = new WebResourceFileSystemProvider(client);
context.subscriptions.push(
    vscode.workspace.registerFileSystemProvider(WEB_RESOURCE_SCHEME, webResourceFsp, {
        isCaseSensitive: true,
        isReadonly: false,
    })
);
```

- [ ] **Step 3: Wire panel row click to FSP**

Update `WebResourcesPanel.ts` `handleMessage` for `openWebResource`:
- For text types: build FSP URI, call `vscode.window.showTextDocument(uri)`
- For binary types: show info message "Binary web resources cannot be edited"
- Trigger unpublished change detection flow (parallel fetch + diff if different)

- [ ] **Step 4: Add filesystem scheme to package.json**

```json
"contributes": {
    "resourceLabelFormatters": [
        {
            "scheme": "ppds-webresource",
            "label": "${path}",
            "separator": "/"
        }
    ]
}
```

- [ ] **Step 5: Wire auto-refresh — panel subscribes to FSP save events**

In `WebResourcesPanel.ts`, subscribe to `webResourceFsp.onDidSaveWebResource`:
- When a web resource is saved, find the matching row in the panel
- Refresh that single row's modifiedOn without a full reload
- Post `webResourcesLoaded` with updated data

- [ ] **Step 6: Build and verify**

Run: `npm run ext:build && npm run ext:typecheck`

- [ ] **Step 7: Run extension tests**

Run: `npm run ext:test`

---

## Chunk 5: TUI + MCP

### Task 14: Create TUI WebResourcesScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs`
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

- [ ] **Step 1: Create the TUI screen**

Follow `ImportJobsScreen.cs` pattern:
- Extends `TuiScreenBase`
- Data table with columns: Name, Display Name, Type, Managed, Modified By, Modified On
- Default sort: name ascending
- Solution filter via `Ctrl+F` dialog (loads solutions, persists selection)
- Text-only toggle via `Ctrl+T`
- `CancellationTokenSource` swap on filter change for staleness protection
- Hotkeys:
  - `Ctrl+R`: Refresh
  - `Enter`: Open content dialog (text types only — read-only `WebResourceContentDialog`)
  - `Ctrl+P`: Publish selected (with `PublishConfirmDialog`)
  - `Ctrl+F`: Solution filter dialog
  - `Ctrl+T`: Toggle text-only/all
  - `Ctrl+O`: Open in Maker Portal

```csharp
public class WebResourcesScreen : TuiScreenBase
{
    private readonly IWebResourceService _webResourceService;
    private readonly ISolutionService _solutionService;
    private CancellationTokenSource? _loadCts;
    private Guid? _selectedSolutionId;
    private bool _textOnly = true;

    // ... constructor, LoadDataAsync, RegisterHotkeys, etc.
    // Follow ImportJobsScreen.cs patterns closely
}
```

- [ ] **Step 2: Register in TuiShell.cs**

Add menu item in Tools menu (after Import Jobs, ~line 283):

```csharp
new MenuItem("Web Resources", "", () => NavigateToWebResources()),
```

Add navigation method (after `NavigateToImportJobs`, ~line 486):

```csharp
private void NavigateToWebResources()
{
    Application.MainLoop?.AddIdle(() =>
    {
        var screen = new WebResourcesScreen(
            EnvironmentUrl,
            _serviceProvider.GetRequiredService<IWebResourceService>(),
            _serviceProvider.GetRequiredService<ISolutionService>(),
            _errorService);
        NavigateTo(screen);
        return false;
    });
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build PPDS.sln -v q`

### Task 15: Create MCP tools

**Files:**
- Create: `src/PPDS.Mcp/Tools/WebResourcesListTool.cs`
- Create: `src/PPDS.Mcp/Tools/WebResourcesGetTool.cs`
- Create: `src/PPDS.Mcp/Tools/WebResourcesPublishTool.cs`

- [ ] **Step 1: Create WebResourcesListTool**

Follow `ImportJobsListTool.cs` pattern:

```csharp
[McpServerToolType]
public class WebResourcesListTool
{
    private readonly McpToolContext _context;

    public WebResourcesListTool(McpToolContext context) => _context = context;

    [McpServerTool(Name = "ppds_web_resources_list")]
    [Description("List web resources in a Dataverse environment, optionally filtered by solution")]
    public async Task<string> ExecuteAsync(
        [Description("Solution ID to filter by")] string? solutionId = null,
        [Description("Only return text-based web resources (default: true)")] bool textOnly = true,
        [Description("Maximum results to return")] int top = 100,
        CancellationToken cancellationToken = default)
    {
        await using var sp = await _context.CreateServiceProviderAsync(cancellationToken);
        var service = sp.GetRequiredService<IWebResourceService>();

        Guid? parsedSolutionId = solutionId != null ? Guid.Parse(solutionId) : null;
        var resources = await service.ListAsync(parsedSolutionId, textOnly, top, cancellationToken);

        // Format as structured text for AI consumption
        // ...
    }
}
```

- [ ] **Step 2: Create WebResourcesGetTool**

Returns decoded content for text types — enables AI analysis of web resource code.

- [ ] **Step 3: Create WebResourcesPublishTool**

Publishes specific web resources by ID — enables AI-driven publish workflows.

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -v q`

---

## Chunk 6: Tests + Polish

### Task 16: Unit tests for WebResourceService

**Files:**
- Create: `tests/PPDS.Dataverse.Tests/Services/WebResourceServiceTests.cs`

- [ ] **Step 1: Write service unit tests**

Test cases:
- `ListAsync_ReturnsAllResources_WhenNoFilter`
- `ListAsync_FiltersTextOnly_WhenTextOnlyTrue`
- `ListAsync_FiltersBySolution_WhenSolutionIdProvided`
- `ListAsync_ReturnsEmpty_WhenSolutionHasNoWebResources`
- `GetContentAsync_UsesRetrieveUnpublished_WhenPublishedFalse`
- `GetContentAsync_UsesStandardQuery_WhenPublishedTrue`
- `GetContentAsync_DecodesBase64Content`
- `GetModifiedOnAsync_ReturnsTimestamp`
- `GetModifiedOnAsync_ReturnsNull_WhenNotFound`
- `UpdateContentAsync_ThrowsNotEditable_ForBinaryTypes`
- `UpdateContentAsync_EncodesContentAsBase64`
- `PublishAsync_BuildsCorrectParameterXml`
- `PublishAsync_ThrowsInProgress_WhenConcurrentPublish`

### Task 17: Unit tests for PooledClientExtensions

**Files:**
- Create: `tests/PPDS.Dataverse.Tests/Pooling/PooledClientExtensionsTests.cs`

- [ ] **Step 1: Write extension method tests**

Test cases:
- `RetrieveUnpublishedAsync_CallsCorrectRequest`
- `PublishXmlAsync_AcquiresAndReleasesLock`
- `PublishXmlAsync_ThrowsInProgress_WhenLockHeld`
- `PublishAllXmlAsync_SharesLockWithPublishXml`

### Task 18: Unit tests for WebResourcesPanel

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/panels/webResourcesPanel.test.ts`

- [ ] **Step 1: Write panel unit tests**

Test cases:
- Request versioning discards stale responses
- Solution filter persists selection
- Text-only toggle updates filter
- Row click dispatches correct message
- Publish selected sends correct IDs

### Task 19: Unit tests for FileSystemProvider

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/providers/webResourceFileSystemProvider.test.ts`

- [ ] **Step 1: Write FSP unit tests**

Test cases:
- `readFile_routesByContentMode`
- `readFile_deduplicatesConcurrentFetches`
- `writeFile_skipsUnchangedContent`
- `writeFile_detectsConflict_whenModifiedOnDiffers`
- `writeFile_updatesServerStateAfterSave`
- `stat_returnsReadonly_forBinaryTypes`
- `parseWebResourceUri_extractsComponents`
- `createWebResourceUri_buildsCorrectFormat`
- `getLanguageId_mapsCorrectly`

### Task 20: Acceptance criteria verification

- [ ] **Step 1: Verify all 18 ACs**

| AC | Description | Verification |
|----|-------------|--------------|
| AC-WR-01 | IWebResourceService created | Service exists with all methods |
| AC-WR-02 | webResources/list with filters | RPC endpoint functional |
| AC-WR-03 | webResources/get with RetrieveUnpublished | Returns unpublished content |
| AC-WR-04 | webResources/publish + publishAll | Publish operations work |
| AC-WR-05 | FSP registers ppds-webresource scheme | Scheme registered in extension |
| AC-WR-06 | On open: unpublished change detection | Diff shown when versions differ |
| AC-WR-07 | On save: conflict detection flow | Conflict modal with resolution |
| AC-WR-08 | On save: non-modal notification + Publish | Notification with button |
| AC-WR-09 | Auto-refresh on save | Panel row updates on FSP save |
| AC-WR-10 | Publish coordination | Concurrent publishes prevented |
| AC-WR-11 | Solution filter at scale | SDK ConditionOperator.In works |
| AC-WR-12 | Request versioning | Stale responses discarded |
| AC-WR-13 | Panel with virtual table | Full panel UI functional |
| AC-WR-14 | Language mode detection | Correct highlighting on open |
| AC-WR-15 | TUI screen with content dialog | TUI functional |
| AC-WR-16 | MCP tools with content | MCP tools return data |
| AC-WR-17 | Virtual table 1000+ resources | Pagination/performance OK |
| AC-WR-18 | Binary type protection | Clear error on edit attempt |

- [ ] **Step 2: Run full test suite**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Run: `npm run ext:test`

- [ ] **Step 3: Run quality gates**

Run `/gates` to verify typecheck, lint, and tests all pass.

---

## Acceptance Criteria Traceability

| AC | Chunk | Task |
|----|-------|------|
| AC-WR-01 | 1 | Tasks 4-5 |
| AC-WR-02 | 2 | Task 6 |
| AC-WR-03 | 2 | Task 6 |
| AC-WR-04 | 2 | Task 6 |
| AC-WR-05 | 4 | Tasks 12-13 |
| AC-WR-06 | 4 | Task 13 |
| AC-WR-07 | 4 | Task 13 |
| AC-WR-08 | 4 | Task 13 |
| AC-WR-09 | 4 | Task 13 (Step 5) |
| AC-WR-10 | 1 | Task 2 (extension methods) |
| AC-WR-11 | 1 | Task 5 (service) |
| AC-WR-12 | 3 | Tasks 9-10 |
| AC-WR-13 | 3 | Tasks 9-11 |
| AC-WR-14 | 4 | Task 12 |
| AC-WR-15 | 5 | Task 14 |
| AC-WR-16 | 5 | Task 15 |
| AC-WR-17 | 3 | Task 10 |
| AC-WR-18 | 1+4 | Tasks 5, 13 |
