# PPDS Core Libraries — Onboarding Guide

A new-contributor walkthrough of the **core libraries** of the Power Platform Developer Suite: the shared Application Services and the MCP server that sit beneath every PPDS surface.

> **Scope.** This guide covers the libraries under `src/` — **PPDS.Dataverse, PPDS.Auth, PPDS.Query, PPDS.Migration, PPDS.Mcp, PPDS.Plugins, PPDS.Analyzers**. The UI surfaces of **PPDS.Cli** and **PPDS.Extension** are out of scope: they are thin shells that map user actions onto services. (PPDS.Cli also hosts the Application Services themselves, in `src/PPDS.Cli/Services/`.) For setup, build and test commands, see [CONTRIBUTING.md](../CONTRIBUTING.md); for the extension, see [src/PPDS.Extension/DEVELOPMENT.md](../src/PPDS.Extension/DEVELOPMENT.md).
>
> **Keeping it current.** The first draft was produced from a generated knowledge graph of `src/` at commit `e67fd952`, then curated by hand. It is ordinary prose documentation: edit it when the architecture changes.

---

## 1. Project Overview

**PPDS (Power Platform Developer Suite)** is a multi-surface .NET platform for Microsoft Power Platform and Dataverse. It ships a CLI, a TUI, a VS Code extension, an MCP server, and NuGet libraries — each surface independently consumable.

The governing rule of the whole codebase (see `specs/CONSTITUTION.md` and `AGENTS.md`):

> **All business logic lives in Application Services — never in UI code.**

Every surface is a thin adapter over the same services. That is why this guide is structured around the libraries, not the surfaces.

- **Language:** C# (.NET 8/9/10; `PPDS.Plugins` targets `net462` for the Dataverse plugin sandbox)
- **Key dependencies:** Microsoft.PowerPlatform.Dataverse.Client, Microsoft.SqlServer.TransactSql.ScriptDom (T‑SQL parsing), ModelContextProtocol, MSAL / Azure.Identity, Microsoft.Extensions.DependencyInjection / Hosting, MinVer
- **Architecture checks:** a dedicated Roslyn analyzer package (`PPDS.Analyzers`) flags violations of the platform's own conventions in PPDS.Cli and PPDS.Mcp

---

## 2. Architecture Layers

The core libraries decompose into eight cohesive layers. Dependencies generally flow **downward**: surfaces → services → connectivity/auth → SDK.

| Layer | Project(s) | What it does |
|---|---|---|
| **Plugin Registration Attributes** | `PPDS.Plugins` | Standalone `net462` package of declarative C# attributes (`PluginStep`, `PluginImage`, `CustomApi`, `CustomApiParameter`) that replace the Dataverse Plugin Registration Tool. **Zero runtime dependencies** on the rest of PPDS — the cleanest place to start reading. |
| **Authentication & Profiles** | `PPDS.Auth` | The credential backbone every surface shares: all MSAL/Azure.Identity flows (device code, client secret, certificate, managed identity, OIDC federation, interactive browser), OS-native credential stores via P/Invoke (Windows DPAPI, macOS Keychain, Linux libsecret), profile storage, multi-cloud endpoint resolution, and environment discovery. |
| **Dataverse Connectivity & Bulk Operations** | `PPDS.Dataverse` | The performance core: multi-strategy connection pooling, bulk CRUD with degree-of-parallelism control, metadata services, resilience/throttle policies, plugin-trace diagnostics, and DI registration. |
| **Generated Dataverse Entities** | `PPDS.Dataverse/Generated` | Auto-generated early-bound entity and option-set classes from the Dataverse schema, giving compile-time column type safety. **Do not hand-edit** — regenerated via `pac modelbuilder`. |
| **SQL Query Engine** | `PPDS.Query` + `PPDS.Dataverse/Query` | The crown jewel: T‑SQL → Dataverse translation. ScriptDom parsing, a Volcano-model physical plan, FetchXML transpilation, TDS execution, a full ADO.NET provider, and SQL IntelliSense. Split across two projects by dependency direction (library API vs. Dataverse-coupled execution). |
| **Data Migration Engine** | `PPDS.Migration` | Parallel export and tiered import for moving data between environments: CMT-format adapters, dependency-ordered import, schema generation, user mapping, and progress reporting. |
| **MCP Server** | `PPDS.Mcp` | A .NET global tool exposing 40+ Dataverse capabilities as Model Context Protocol tools for AI assistants. The most self-contained **runnable** surface in scope — and a textbook example of the surface→service pattern. |
| **Roslyn Analyzers** | `PPDS.Analyzers` | 16 compile-time rules covering the platform's architecture (Application-Service boundaries, async hygiene, Dataverse performance patterns, structured exceptions, public-API docs). Applied to PPDS.Cli and PPDS.Mcp. |

---

## 3. Key Concepts & Conventions

These patterns recur everywhere. Learn them once, recognize them everywhere.

- **Application Services hold all logic.** A surface handler (CLI command, MCP tool, extension panel) only maps parameters and translates errors; it resolves an `I…Service` from DI and delegates. If you find business logic in a handler, that's a bug.
- **Connection pooling is the performance primitive.** `IDataverseConnectionPool` hands out pooled `ServiceClient`s for parallel workloads. **The critical rule:** never reuse a single checked-out client across multiple concurrent awaited calls — each parallel branch checks out its own. Analyzer **PPDS007** (`PoolClientInParallelAnalyzer`) flags it, at suggestion severity, so the build will not stop you.
- **Errors are wrapped, never raw.** Application Services wrap exceptions in `PpdsException` with an `ErrorCode` (flagged by analyzer **PPDS004**, `UseStructuredExceptionsAnalyzer`).
- **`stdout` is reserved for data.** Services never write status to `stdout`; use `Console.Error` / an injected writer (flagged by **PPDS002**, `NoConsoleInServicesAnalyzer`). Note the auth library's injectable `Action<string>` writer pattern instead of direct console access.
- **Strongly-typed everywhere.** Metadata comes back as DTOs (`EntityMetadataDto`, `AttributeMetadataDto`, …), not raw SDK objects; columns are accessed through generated early-bound entities, not string keys.
- **`IProgressReporter` for anything slow.** Long-running operations thread progress events through so CLI/TUI/MCP all get live feedback.
- **PPDS.Auth's public API is tracked.** `src/PPDS.Auth/PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` record its exported surface; RS0016/RS0017 are build errors there. Don't hand-merge them during a rebase (see CONTRIBUTING.md).

---

## 4. Guided Tour (recommended reading order)

A 14-stop path from the simplest library to the full request flow. Each stop names the few files worth opening first.

**1. Project Overview — `PPDS.Mcp/README.md`.** Grasp the purpose before touching code. The MCP server is the most self-contained runnable surface; its README shows the full tool catalogue and how each tool category maps to a library underneath.

**2. Plugin Registration Attributes — `PPDS.Plugins/Attributes/`.** The simplest library: four declarative attributes that replace the Plugin Registration Tool. Start with `PluginStepAttribute.cs`, `CustomApiAttribute.cs`, `PluginImageAttribute.cs`. Builds the vocabulary (step stages, execution modes, image types) you'll see everywhere. Zero dependencies on other PPDS libs.

**3. Authentication & Profiles — `PPDS.Auth/Credentials/` + `Profiles/`.** `ICredentialProvider` is the single abstraction behind seven concrete strategies; `CredentialProviderFactory` selects one from an `AuthProfile`; `ProfileStore` persists profiles as encrypted JSON. This separation is why all surfaces authenticate identically.

**4. Environment Discovery & ServiceClient Factory — `PPDS.Auth/Discovery/` + `ServiceClientFactory.cs`.** `EnvironmentResolutionService` turns a user identifier (URL, name, GUID) into a resolved `EnvironmentInfo` via Global Discovery + the BAP API. `ServiceClientFactory` then produces the SDK-level `ServiceClient`. These bridge auth profiles to the raw SDK client the pool manages.

**5. Connection Pooling — `PPDS.Dataverse/Pooling/`.** `IDataverseConnectionPool` is the most-referenced interface in the codebase. `DataverseConnectionPool` maintains pooled `ServiceClient`s and applies a pluggable `IConnectionSelectionStrategy` (RoundRobin / LeastConnections / ThrottleAware). Re-read the "never reuse a checked-out client" rule from §3 here.

**6. Bulk Operations — `PPDS.Dataverse/BulkOperations/`.** `IBulkOperationExecutor` wraps Dataverse `ExecuteMultiple`/`CreateMultiple`/`UpdateMultiple` with adaptive batch sizing and DOP-controlled parallelism, fanning batches across pooled clients. `AdaptiveBatchSizer` tunes batch size from observed latency. This is the engine under the migration layer (stop 11).

**7. Metadata Services & Generated Entities — `PPDS.Dataverse/Metadata/` + `Generated/`.** `IMetadataQueryService` returns typed metadata DTOs; `DataverseMetadataQueryService` implements it, with `CachedMetadataProvider` on top. The `Generated/` early-bound classes give compile-time column access. Both the query and migration engines rely on these to resolve table/column names at plan time.

**8. SQL Query Engine — Parsing & Planning — `PPDS.Query/Parsing/` + `Planning/`.** `QueryParser` wraps `TSql170Parser` (ScriptDom) to produce a validated AST. `ExecutionPlanBuilder` converts it into a Volcano-model tree of `IQueryPlanNode`s (handling SELECT, JOINs — nested-loop/hash/merge — CTEs, window functions, aggregates, DML). `ExecutionPlanOptimizer` applies predicate pushdown and constant folding.

**9. SQL Query Engine — Transpilation & Execution — `PPDS.Query/Transpilation/` + `PPDS.Dataverse/Query/Execution/`.** `FetchXmlGenerator` transpiles parsed T‑SQL into FetchXML. `PlanExecutor` drives the plan tree to a `QueryResult`; `QueryExecutor` does the Dataverse page-walking and value conversion for scan leaves. `TdsQueryExecutor` is the parallel path over the Dataverse TDS endpoint.

**10. ADO.NET Provider — `PPDS.Query/Provider/`.** A full ADO.NET provider so any `DbConnection`-speaking tool (SSMS add-ins, LINQPad) can query Dataverse with standard SQL. `PpdsDbConnection` manages state and DML confirmation events (PreInsert/PreUpdate/PreDelete); `PpdsDbCommand` wraps execution; `PpdsDbProviderFactory` is the registered factory. This is how `QuerySqlTool` ultimately submits queries.

**11. Data Migration Engine — `PPDS.Migration/`.** `TieredImporter` is the orchestration heart: it reads CMT-format data, uses `DependencyGraphBuilder` to compute import order (resolving circular lookups via strongly-connected-component analysis), then runs multi-phase parallel imports (create → deferred-fields → relationship → state-transition). `ParallelExporter` handles GUID-partitioned outbound reads. `IProgressReporter` threads progress throughout.

**12. MCP Server — Entry Point & Composition Root — `PPDS.Mcp/Program.cs` + `Infrastructure/`.** `Program.cs` is the only runnable entry point in scope: it configures the DI host, registers the Auth/Dataverse/Query/Migration services, and wires `McpConnectionPoolManager` for per-session pool lifecycle. `McpToolBase.CreateScopeAsync` creates a per-request DI scope and resolves the target service — the surface→service pattern in action.

**13. MCP Tool Handlers in Practice — `PPDS.Mcp/Tools/`.** 40+ `McpToolBase` subclasses, one capability each. `QuerySqlTool` → ADO.NET provider (stop 10); `MetadataCreateTableTool` → metadata authoring (stop 7); `PluginsListTool` → generated entities (stop 7). **Trace one tool end-to-end** — e.g. `QuerySqlTool → PpdsDbCommand → PlanExecutor → QueryExecutor → FetchXML → Dataverse` — and you've connected every layer in this tour.

**14. Roslyn Analyzers — `PPDS.Analyzers/Rules/`.** 16 compile-time rules. `DiagnosticIds.cs` is the rule registry; `NoConsoleInServicesAnalyzer` (PPDS002), `PoolClientInParallelAnalyzer` (PPDS007), and `UseStructuredExceptionsAnalyzer` (PPDS004) encode the conventions from §3. PPDS001–PPDS013 are at suggestion severity in `.editorconfig`, so they appear in the IDE but do not fail the build; PPDS014–PPDS016 are warnings, which PPDS.Cli and PPDS.Mcp treat as errors. Their tests are in `tests/PPDS.Analyzers.Tests`.

---

## 5. Complexity Hotspots

The biggest, densest files — approach these with care, and don't expect to read them top-to-bottom on day one.

| File | Layer | Why it's heavy |
|---|---|---|
| `PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` | Connectivity | ~1,900 lines: parallel/sequential batched CRUD with throttle handling, retry, and error policy |
| `PPDS.Query/Transpilation/FetchXmlGenerator.cs` | SQL Engine | ~1,700 lines, 50+ methods: the core SQL→FetchXML transpiler |
| `PPDS.Dataverse/Pooling/DataverseConnectionPool.cs` | Connectivity | ~1,500 lines: multi-source pool with throttle-aware selection and background validation |
| `PPDS.Migration/Import/TieredImporter.cs` | Migration | ~1,300 lines: multi-phase import orchestration |
| `PPDS.Dataverse/Query/Planning/Nodes/ClientWindowNode.cs` | SQL Engine | SQL window functions implemented entirely client-side |
| `PPDS.Query/.../WindowSpoolNode.cs` | SQL Engine | ~860 lines: ROW_NUMBER/RANK/LAG/LEAD/NTILE + framed aggregates runtime |
| `PPDS.Dataverse/Metadata/DataverseMetadataQueryService.cs` | Connectivity | ~850 lines: full SDK-metadata → DTO mapping |
| `PPDS.Auth/Internal/CredentialStore/**` (Windows/macOS/Linux) | Auth | P/Invoke against DPAPI, Keychain (CoreFoundation + Security.framework), libsecret |

Beyond these, the **`PPDS.Dataverse/Query/Planning/Nodes/`** and **`PPDS.Query/Planning/Nodes/`** directories together form the Volcano-model plan-node hierarchy (all implementing `IQueryPlanNode`) — the densest *conceptual* area, even where individual files are small.

---

## 6. First-Day Checklist

1. Read `AGENTS.md` and Part 1 of `specs/CONSTITUTION.md` (the architecture principles).
2. Read `PPDS.Mcp/README.md` and `PPDS.Dataverse/README.md`.
3. Walk tour stops 1–7 in code; skim 8–14.
4. Internalize the pool-parallelism rule and the `PpdsException` convention — reviewers will hold you to them, and the analyzers only suggest.
