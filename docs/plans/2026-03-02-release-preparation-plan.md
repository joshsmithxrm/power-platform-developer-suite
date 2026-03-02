# March 2026 Release Preparation - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prepare all PPDS packages for NuGet release — update changelogs, create new package docs, update CI workflows, and merge dependabot PRs.

**Architecture:** Documentation and CI-only changes. No code changes. Each package has its own CHANGELOG.md following Keep a Changelog format. MinVer determines versions from git tags. publish-nuget.yml handles NuGet publishing on tag push.

**Tech Stack:** Markdown (changelogs, READMEs), YAML (GitHub Actions), git (tags)

---

### Task 1: Merge Dependabot PRs

**Step 1: Merge PR #555 (minimatch security fix)**

Run: `cd /c/VS/ppdsw/ppds && gh pr merge 555 --merge`

**Step 2: Merge PR #554 (@types/node patch)**

Run: `cd /c/VS/ppdsw/ppds && gh pr merge 554 --merge`

**Step 3: Pull merged changes**

Run: `cd /c/VS/ppdsw/ppds && git pull`

---

### Task 2: Update publish-nuget.yml

**Files:**
- Modify: `.github/workflows/publish-nuget.yml`

**Step 1: Add Query-v* and Mcp-v* tag triggers**

In the `on.push.tags` section, add:
```yaml
      - 'Query-v*'
      - 'Mcp-v*'
```

**Step 2: Add project mappings in the determine-package step**

Add these elif blocks before the `else` block:
```bash
elif [[ $TAG == Query-v* ]]; then
  echo "package=PPDS.Query" >> $GITHUB_OUTPUT
  echo "projects=src/PPDS.Query/PPDS.Query.csproj" >> $GITHUB_OUTPUT
elif [[ $TAG == Mcp-v* ]]; then
  echo "package=PPDS.Mcp" >> $GITHUB_OUTPUT
  echo "projects=src/PPDS.Mcp/PPDS.Mcp.csproj" >> $GITHUB_OUTPUT
```

**Step 3: Verify the file looks correct**

Read the modified file and confirm the structure is correct.

**Step 4: Commit**

```bash
git add .github/workflows/publish-nuget.yml
git commit -m "ci: add PPDS.Query and PPDS.Mcp to NuGet publish workflow"
```

---

### Task 3: Create PPDS.Query CHANGELOG.md

**Files:**
- Create: `src/PPDS.Query/CHANGELOG.md`

**Step 1: Write the changelog**

Content should follow Keep a Changelog format. Version: `1.0.0-beta.1`. Include all user-facing features from the 109 commits grouped by category:

**Added section highlights:**
- Full T-SQL parsing via Microsoft.SqlServer.TransactSql.ScriptDom
- FetchXML transpilation from SQL AST
- Execution plan engine with Volcano iterator model (streaming results)
- ADO.NET provider (PpdsDbConnection, PpdsDbCommand, PpdsDbDataReader) for standard .NET data access
- PpdsDbProviderFactory for ADO.NET provider discoverability
- SELECT with columns, aliases, TOP, DISTINCT
- WHERE with all comparison operators, LIKE, IN, BETWEEN, IS NULL, IS NOT DISTINCT FROM
- JOIN support: INNER, LEFT, RIGHT, FULL OUTER, CROSS, OUTER APPLY — pushdown to FetchXML when possible, client-side hash/merge/nested-loop fallback
- Multi-column key support in client-side JOIN ON conditions
- GROUP BY with aggregates (COUNT, SUM, AVG, MIN, MAX, STDEV, STDEVP, VAR, VARP) including expression GROUP BY
- HAVING clause with aggregate predicate support
- ORDER BY with ASC/DESC
- UNION and UNION ALL
- Subqueries: IN (SELECT), NOT IN, EXISTS, NOT EXISTS, derived tables (FROM subquery), scalar subqueries
- NOT IN subquery rewrite to LEFT OUTER JOIN for FetchXML pushdown
- Window functions: ROW_NUMBER, RANK, DENSE_RANK, CUME_DIST, PERCENT_RANK with PARTITION BY and ORDER BY
- Common Table Expressions (WITH ... AS) including recursive CTEs
- CASE/WHEN/THEN/ELSE and IIF expressions
- Computed column expressions in SELECT
- Expression evaluation: ISNULL, COALESCE, NULLIF, CAST, string functions, date functions (DATEADD, DATEDIFF, DATEPART, YEAR, MONTH, DAY, GETDATE, GETUTCDATE, TIMEFROMPARTS)
- DML: INSERT, UPDATE, DELETE with safety guards and row caps
- INSERT ... SELECT with ordinal mapping
- SELECT INTO #temp for script-scoped temporary tables
- DECLARE/SET variable assignment (SELECT @var = expr)
- IF/ELSE flow control for multi-statement scripts
- WHILE loops with BREAK and CONTINUE
- OPENJSON table-valued function
- JSON_MODIFY with array path support
- OPTION() query hints and comment-based hint parser
- EXPLAIN command for query plan inspection with pool/parallelism metadata
- TDS Endpoint routing for compatible queries
- Parallel partitioned aggregates for accurate COUNT(*) beyond Dataverse 50K limit
- Adaptive aggregate retry with binary date-range splitting when partitions exceed limits
- Prefetch scan node for page-ahead buffering (improved streaming throughput)
- Metadata query system (queryable entity/attribute schema via INFORMATION_SCHEMA-style access)
- DML safety guard with configurable thresholds, environment protection levels, and cross-environment DML policy enforcement
- Cross-environment query planning with bracket syntax ([environment].[entity])
- Smart label detection for 2-part cross-environment references
- RemoteScanNode for cross-environment FetchXML execution
- IndexSpoolNode for correlated subquery caching
- TableSpoolNode for in-memory result materialization
- QueryExecutionException with structured error codes
- @@ERROR and ERROR_MESSAGE() tracking in session context
- Paging cap and materialization limit safety features

**Step 2: Commit**

```bash
git add src/PPDS.Query/CHANGELOG.md
git commit -m "docs: add PPDS.Query CHANGELOG with v1.0.0-beta.1 release notes"
```

---

### Task 4: Create PPDS.Query README.md

**Files:**
- Create: `src/PPDS.Query/README.md`

**Step 1: Write the README**

Follow the pattern from `src/PPDS.Dataverse/README.md` — installation, quick start with code example, features list, target frameworks, license. Key sections:

- Installation (`dotnet add package PPDS.Query`)
- Quick Start: ADO.NET provider usage (PpdsDbConnection)
- Quick Start: CLI usage (`ppds query sql "SELECT name FROM account WHERE revenue > 1000000"`)
- Features: SQL support matrix, execution engine, safety features, cross-env queries
- Target Frameworks: net8.0, net9.0, net10.0
- License: MIT

**Step 2: Commit**

```bash
git add src/PPDS.Query/README.md
git commit -m "docs: add PPDS.Query README"
```

---

### Task 5: Create PPDS.Mcp CHANGELOG.md

**Files:**
- Create: `src/PPDS.Mcp/CHANGELOG.md`

**Step 1: Write the changelog**

Version: `1.0.0-beta.1`. Features from 11 commits:

**Added:**
- MCP server exposing Power Platform capabilities for AI assistant integration
- Distributed as .NET tool (`ppds-mcp-server`)
- 12+ read-only Dataverse tools for Claude Code and other MCP clients
- SQL and FetchXML query execution via MCP tools
- Entity metadata exploration
- Plugin registration and trace log analysis
- DI-based architecture with injected ProfileStore and auth services
- Integration with PPDS.Query engine (ScriptDom parser, FetchXML generator)
- Supports all PPDS.Auth authentication methods

**Step 2: Commit**

```bash
git add src/PPDS.Mcp/CHANGELOG.md
git commit -m "docs: add PPDS.Mcp CHANGELOG with v1.0.0-beta.1 release notes"
```

---

### Task 6: Create PPDS.Mcp README.md

**Files:**
- Create: `src/PPDS.Mcp/README.md`

**Step 1: Write the README**

Follow the pattern from other package READMEs. Key sections:

- Installation (`dotnet tool install -g PPDS.Mcp`)
- Quick Start: Claude Code configuration (settings.json snippet)
- Available Tools: list of MCP tools with descriptions
- Authentication: how to set up profiles
- Target Frameworks: net8.0, net9.0, net10.0
- License: MIT

**Step 2: Commit**

```bash
git add src/PPDS.Mcp/README.md
git commit -m "docs: add PPDS.Mcp README"
```

---

### Task 7: Update PPDS.Auth CHANGELOG.md

**Files:**
- Modify: `src/PPDS.Auth/CHANGELOG.md`

**Step 1: Populate the [Unreleased] section with v1.0.0-beta.7 release notes**

Based on the 22 commits since Auth-v1.0.0-beta.6:

**Added:**
- `AddAuthServices()` DI registration extension for ProfileStore, EnvironmentConfigStore, and NativeCredentialStore
- `EnvironmentConfig` models and `EnvironmentConfigStore` for per-environment configuration (label, type, color)
- `EnvironmentType` enum (Development, Test, Production, Sandbox, Default)
- `QuerySafetySettings` in `EnvironmentConfig` for per-environment DML safety thresholds
- `ProtectionLevel` support for environment protection enforcement
- Cross-environment DML policy enforcement

**Changed:**
- `EnvironmentConfig.Type` changed from `string` to `EnvironmentType` enum

**Fixed:**
- Token cache scope mismatch in browser auth profile creation
- Disposal guards, empty-string-clears, and typed catches in credential providers

Replace the empty `## [Unreleased]` section with versioned `## [1.0.0-beta.7]` and add new empty `## [Unreleased]` above it. Update comparison links at bottom.

**Step 2: Commit**

```bash
git add src/PPDS.Auth/CHANGELOG.md
git commit -m "docs: update PPDS.Auth CHANGELOG for v1.0.0-beta.7"
```

---

### Task 8: Update PPDS.Dataverse CHANGELOG.md

**Files:**
- Modify: `src/PPDS.Dataverse/CHANGELOG.md`

**Step 1: Finalize the [Unreleased] section as v1.0.0-beta.6 release notes**

The existing Unreleased section already has good content. Enhance it with:

**Added (additional items):**
- Child record paging boundary detection for linked entities
- Adaptive thread management for 429 backoff (reduces thread consumption during throttling)
- Auto-paging for RemoteScanNode

**Fixed:**
- Floating-point equality comparisons for SQL semantics
- Use TryGetValue instead of ContainsKey + indexer pattern

Rename `## [Unreleased]` to `## [1.0.0-beta.6]` with date, add new empty `## [Unreleased]` above it. Update comparison links.

**Step 2: Commit**

```bash
git add src/PPDS.Dataverse/CHANGELOG.md
git commit -m "docs: update PPDS.Dataverse CHANGELOG for v1.0.0-beta.6"
```

---

### Task 9: Update PPDS.Cli CHANGELOG.md

**Files:**
- Modify: `src/PPDS.Cli/CHANGELOG.md`

**Step 1: Finalize the [Unreleased] section as v1.0.0-beta.13 release notes**

The existing Unreleased section has some content but is missing major features. Add:

**Added (additional items):**
- Multi-tab architecture with per-environment query tabs
- `ppds env config` command for setting environment label, type, and color
- `ppds env type` command
- EnvironmentConfigDialog for configuring environment label, type (dropdown), and color
- DeviceCodeDialog with selectable code text and auto-close on auth completion
- TDS Endpoint toggle with Ctrl+T shortcut
- F7/F8/F9 keybindings for Linux terminal compatibility
- Escape key to cancel running queries
- Certificate Store and Username/Password auth methods in TUI
- Environment selector with resolved labels, preview panel, and config access
- DML safety wiring with environment-specific settings and protection levels
- Cross-environment query support

**Changed:**
- Major DI refactoring: AuthServices, EnvironmentConfigStore, ProfileStore injection throughout CLI and TUI
- Migrated from legacy SQL parser to PPDS.Query engine (ScriptDom-based)
- Deleted legacy parser, AST, transpiler, and evaluator (-22K lines)

**Fixed (additional items):**
- Timer and event leaks in TUI
- Splitter drag issues
- Autocomplete interference during paste operations
- Tab highlight color regression
- Many CodeQL code scanning alerts resolved (70 findings)

Rename `## [Unreleased]` to `## [1.0.0-beta.13]` with date, add new empty `## [Unreleased]` above it. Update comparison links.

**Step 2: Commit**

```bash
git add src/PPDS.Cli/CHANGELOG.md
git commit -m "docs: update PPDS.Cli CHANGELOG for v1.0.0-beta.13"
```

---

### Task 10: Update PPDS.Migration CHANGELOG.md

**Files:**
- Modify: `src/PPDS.Migration/CHANGELOG.md`

**Step 1: Add v1.0.0-beta.7 release notes**

Based on 4 commits (dependency updates only):

**Changed:**
- Updated Microsoft.Data.SqlClient from 5.2.2 to 6.1.4

Rename `## [Unreleased]` to `## [1.0.0-beta.7]` with date, add new empty `## [Unreleased]` above it. Update comparison links.

**Step 2: Commit**

```bash
git add src/PPDS.Migration/CHANGELOG.md
git commit -m "docs: update PPDS.Migration CHANGELOG for v1.0.0-beta.7"
```

---

### Task 11: Update Root CHANGELOG.md Index

**Files:**
- Modify: `CHANGELOG.md`

**Step 1: Add PPDS.Query and PPDS.Mcp to the per-package list**

Add after the PPDS.Cli entry:
```markdown
- [PPDS.Query](src/PPDS.Query/CHANGELOG.md) - SQL query engine for Dataverse
- [PPDS.Mcp](src/PPDS.Mcp/CHANGELOG.md) - MCP server for AI assistant integration
```

**Step 2: Add tag formats to versioning table**

Add rows:
```markdown
| PPDS.Query | `Query-v{version}` | `Query-v1.0.0` |
| PPDS.Mcp | `Mcp-v{version}` | `Mcp-v1.0.0` |
```

**Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: add PPDS.Query and PPDS.Mcp to changelog index"
```

---

### Task 12: Update Root README.md

**Files:**
- Modify: `README.md`

**Step 1: Add PPDS.Query to NuGet Libraries table**

Add row after PPDS.Auth:
```markdown
| **PPDS.Query** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Query.svg)](https://www.nuget.org/packages/PPDS.Query/) | SQL query engine with FetchXML transpilation and ADO.NET provider |
```

**Step 2: Add PPDS.Query to Compatibility table**

Add row:
```markdown
| PPDS.Query | net8.0, net9.0, net10.0 |
```

**Step 3: Add `ppds query explain` to CLI Commands table**

Update the query row to include explain and history:
```markdown
| `ppds query` | Execute queries (fetch, sql, explain, history) |
```

**Step 4: Add PPDS.Query section with code example**

Add after the PPDS.Migration section, before Development:

```markdown
## PPDS.Query

SQL query engine for Dataverse with FetchXML transpilation and an ADO.NET provider.

\```bash
dotnet add package PPDS.Query
\```

\```csharp
// ADO.NET provider for standard .NET data access
using var connection = new PpdsDbConnection(pool);
await connection.OpenAsync();

using var command = connection.CreateCommand();
command.CommandText = "SELECT name, revenue FROM account WHERE revenue > @threshold";
command.Parameters.AddWithValue("@threshold", 1_000_000m);

using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader["name"]}: {reader["revenue"]:C}");
}
\```

See [PPDS.Query documentation](src/PPDS.Query/README.md) for details.
```

**Step 5: Commit**

```bash
git add README.md
git commit -m "docs: add PPDS.Query to root README"
```

---

### Task 13: Final Review and Summary Commit

**Step 1: Run `git log --oneline -15` to review all commits made**

Verify each task produced a clean commit.

**Step 2: Run `git diff --stat HEAD~12..HEAD` to see all changed files**

Verify only documentation and CI files were changed.

**Step 3: Verify build still passes**

Run: `dotnet build PPDS.sln --configuration Release`

This ensures no accidental code changes broke anything.
