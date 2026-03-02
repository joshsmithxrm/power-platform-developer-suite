# Cross-Environment Smart Label Detection — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `[LABEL].entity` (2-part name) work as a cross-environment reference without requiring the meaningless `dbo` schema prefix.

**Architecture:** Modify `ExecutionPlanBuilder.PlanTableReference()` to check `SchemaIdentifier` against `RemoteExecutorFactory` when `DatabaseIdentifier`/`ServerIdentifier` are null and the schema is not `dbo`. Also update `ContainsCrossEnvironmentReferenceInTableRef()` to detect 2-part cross-env references for DML safety, and add label validation to reject `dbo` as a profile label.

**Tech Stack:** C#, xUnit, FluentAssertions, Moq, Microsoft.SqlServer.TransactSql.ScriptDom

---

### Task 1: Update existing test — 2-part name `dbo.account` stays local

The existing test `Plan_TwoPartName_DoesNotTriggerCrossEnvironment` currently passes by accident (SchemaIdentifier is ignored). After our changes, we need to ensure `dbo` specifically remains local even when a `RemoteExecutorFactory` is provided. Update the test to be explicit.

**Files:**
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs:793-804`

**Step 1: Update the existing test to include a RemoteExecutorFactory**

The current test uses default options (no factory). After our changes, 2-part names with non-`dbo` schemas will hit the factory. Confirm `dbo` is explicitly excluded.

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_TwoPartName_Dbo_DoesNotTriggerCrossEnvironment()
{
    // dbo.account is a 2-part name (SchemaIdentifier=dbo, BaseIdentifier=account).
    // "dbo" is reserved and must NEVER be treated as a cross-environment label,
    // even when a RemoteExecutorFactory is configured.
    var options = new QueryPlanOptions
    {
        RemoteExecutorFactory = label => label == "dbo" ? Mock.Of<IQueryExecutor>() : null
    };
    var fragment = _parser.Parse("SELECT name FROM dbo.account");
    var result = _builder.Plan(fragment, options);

    result.RootNode.Should().BeAssignableTo<FetchXmlScanNode>(
        "2-part name dbo.account should remain a local FetchXmlScanNode");
}
```

**Step 2: Run test to verify it passes (existing behavior already correct)**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~TwoPartName_Dbo" -v q`
Expected: PASS

**Step 3: Commit**

```
test: make dbo 2-part name test explicit about RemoteExecutorFactory
```

---

### Task 2: RED — Write failing test for 2-part cross-env detection

**Files:**
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs` (add after cross-env section ~line 804)

**Step 1: Write the failing test**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_TwoPartName_MatchesLabel_ProducesRemoteScanNode()
{
    // [UAT].account is a 2-part name (SchemaIdentifier=UAT, BaseIdentifier=account).
    // When "UAT" matches a configured profile label, it should be treated as cross-env.
    var sql = "SELECT name FROM [UAT].account";
    var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
    var options = new QueryPlanOptions
    {
        RemoteExecutorFactory = label => label == "UAT" ? mockRemoteExecutor : null
    };

    var result = _builder.Plan(_parser.Parse(sql), options);

    ContainsNodeOfType<RemoteScanNode>(result.RootNode).Should().BeTrue(
        "2-part name [UAT].account should produce RemoteScanNode when UAT matches a profile label");
    ContainsNodeOfType<TableSpoolNode>(result.RootNode).Should().BeTrue(
        "remote scan should be wrapped in TableSpoolNode");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~TwoPartName_MatchesLabel" -v q`
Expected: FAIL — currently `[UAT].account` is treated as local `FetchXmlScanNode`

---

### Task 3: RED — Write failing test for 2-part unknown label error

**Files:**
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_TwoPartName_UnknownLabel_ThrowsDescriptiveError()
{
    // [STAGING].account where STAGING doesn't match any profile label.
    // Must fail loudly — never silently query the local environment.
    var sql = "SELECT name FROM [STAGING].account";
    var options = new QueryPlanOptions
    {
        RemoteExecutorFactory = label => null  // No matching profile
    };

    var act = () => _builder.Plan(_parser.Parse(sql), options);

    act.Should().Throw<QueryParseException>()
        .WithMessage("*STAGING*");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~TwoPartName_UnknownLabel" -v q`
Expected: FAIL — currently treats `STAGING` as a schema and returns a local node (no exception)

---

### Task 4: GREEN — Implement smart label detection in PlanTableReference

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs:1062-1079`

**Step 1: Implement the change**

Replace the current `NamedTableReference` handling block (lines 1062–1079):

```csharp
if (tableRef is NamedTableReference named)
{
    // Cross-environment reference: ScriptDom parses [LABEL].dbo.entity as a 3-part name
    // with DatabaseIdentifier="LABEL", or [SERVER].[DB].dbo.entity as 4-part with
    // ServerIdentifier="SERVER". Either indicates a cross-environment reference.
    var profileLabel = named.SchemaObject.ServerIdentifier?.Value
        ?? named.SchemaObject.DatabaseIdentifier?.Value;

    if (profileLabel != null)
    {
        return PlanRemoteTableReference(named, profileLabel, options);
    }

    // Smart label detection for 2-part names: [LABEL].entity is parsed as
    // SchemaIdentifier=LABEL, BaseIdentifier=entity. If schema is not "dbo"
    // and a RemoteExecutorFactory is configured, check if it matches a profile label.
    var schemaId = named.SchemaObject.SchemaIdentifier?.Value;
    if (schemaId != null
        && !string.Equals(schemaId, "dbo", StringComparison.OrdinalIgnoreCase)
        && options.RemoteExecutorFactory != null)
    {
        return PlanRemoteTableReference(named, schemaId, options);
    }

    var entityName = GetMultiPartName(named.SchemaObject);
    var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";
    var scanNode = new FetchXmlScanNode(fetchXml, entityName);
    return (scanNode, entityName);
}
```

**Step 2: Run tests to verify all 3 new + existing cross-env tests pass**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~CrossEnvironment|FullyQualifiedName~TwoPartName" -v q`
Expected: ALL PASS

**Step 3: Commit**

```
feat(query): add smart label detection for 2-part cross-env references

[LABEL].entity now resolves as cross-environment when the label matches
a configured profile, without requiring the meaningless dbo schema prefix.
Unknown non-dbo schemas fail with an actionable error instead of silently
querying the local environment.
```

---

### Task 5: RED — Write failing test for ContainsCrossEnvironmentReference with 2-part names

The `ContainsCrossEnvironmentReferenceInTableRef()` method is used by DML safety to detect cross-env operations. It currently only checks `ServerIdentifier`/`DatabaseIdentifier`, so it misses 2-part cross-env references.

**Files:**
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

This method is private, so we test it indirectly. The best approach: find how `ContainsCrossEnvironmentReference` is used in the planner. Let me check.

**Step 1: Find usage of ContainsCrossEnvironmentReference**

Check `ExecutionPlanBuilder.cs` for calls to `ContainsCrossEnvironmentReference`. It's used in DML planning to determine if a statement involves cross-env references. For now, verify the planning-level behavior: a 2-part cross-env JOIN should be correctly identified.

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_TwoPartName_CrossEnvJoin_ProducesRemoteScanNodes()
{
    // JOIN between local and 2-part cross-env reference
    var sql = "SELECT a.name FROM account a JOIN [UAT].contact c ON a.accountid = c.parentcustomerid";
    var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
    var options = new QueryPlanOptions
    {
        RemoteExecutorFactory = label => label == "UAT" ? mockRemoteExecutor : null
    };

    var result = _builder.Plan(_parser.Parse(sql), options);

    ContainsNodeOfType<RemoteScanNode>(result.RootNode).Should().BeTrue(
        "2-part [UAT].contact in JOIN should produce RemoteScanNode");
}
```

**Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~CrossEnvJoin" -v q`
Expected: This may already pass if `PlanTableReference` is called per table in a JOIN. If it fails, the fix in Task 4 should have already handled it. Verify.

---

### Task 6: GREEN — Update ContainsCrossEnvironmentReferenceInTableRef

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs:1229-1241`

**Step 1: Update the detection method**

```csharp
private static bool ContainsCrossEnvironmentReferenceInTableRef(TableReference tableRef)
{
    if (tableRef is NamedTableReference named)
    {
        // 3/4-part names: [DB].dbo.entity or [SERVER].[DB].dbo.entity
        if (named.SchemaObject.ServerIdentifier != null || named.SchemaObject.DatabaseIdentifier != null)
            return true;

        // 2-part names: [LABEL].entity where LABEL is not "dbo"
        var schema = named.SchemaObject.SchemaIdentifier?.Value;
        if (schema != null && !string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    if (tableRef is QualifiedJoin qualified)
        return ContainsCrossEnvironmentReferenceInTableRef(qualified.FirstTableReference)
            || ContainsCrossEnvironmentReferenceInTableRef(qualified.SecondTableReference);
    if (tableRef is UnqualifiedJoin unqualified)
        return ContainsCrossEnvironmentReferenceInTableRef(unqualified.FirstTableReference)
            || ContainsCrossEnvironmentReferenceInTableRef(unqualified.SecondTableReference);
    return false;
}
```

**Step 2: Run all cross-env tests**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "FullyQualifiedName~CrossEnvironment|FullyQualifiedName~TwoPartName|FullyQualifiedName~CrossEnvJoin" -v q`
Expected: ALL PASS

**Step 3: Commit**

```
fix(query): detect 2-part cross-env references in DML safety checks
```

---

### Task 7: RED — Write failing test for "dbo" label rejection

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Services/ProfileResolutionServiceTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void Constructor_RejectsReservedDboLabel()
{
    var configs = new List<EnvironmentConfig>
    {
        new() { Url = "https://dbo.crm.dynamics.com/", Label = "dbo" }
    };

    var act = () => new ProfileResolutionService(configs);

    act.Should().Throw<ArgumentException>()
        .WithMessage("*dbo*reserved*");
}

[Fact]
public void Constructor_RejectsReservedDboLabel_CaseInsensitive()
{
    var configs = new List<EnvironmentConfig>
    {
        new() { Url = "https://dbo.crm.dynamics.com/", Label = "DBO" }
    };

    var act = () => new ProfileResolutionService(configs);

    act.Should().Throw<ArgumentException>()
        .WithMessage("*DBO*reserved*");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --no-restore --filter "FullyQualifiedName~RejectsReservedDbo" -v q`
Expected: FAIL — constructor currently accepts any label

---

### Task 8: GREEN — Implement "dbo" label rejection

**Files:**
- Modify: `src/PPDS.Cli/Services/ProfileResolutionService.cs:14-24`

**Step 1: Add validation to the constructor**

```csharp
public ProfileResolutionService(IEnumerable<EnvironmentConfig> configs)
{
    _labelIndex = new Dictionary<string, EnvironmentConfig>(StringComparer.OrdinalIgnoreCase);
    foreach (var config in configs)
    {
        if (!string.IsNullOrEmpty(config.Label))
        {
            if (string.Equals(config.Label, "dbo", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"'{config.Label}' cannot be used as an environment label (reserved for SQL schema convention).",
                    nameof(configs));

            _labelIndex[config.Label] = config;
        }
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --no-restore --filter "FullyQualifiedName~ProfileResolution" -v q`
Expected: ALL PASS (including existing tests)

**Step 3: Commit**

```
fix(config): reject "dbo" as an environment label (reserved for SQL convention)
```

---

### Task 9: Run full regression suite

**Step 1: Run all unit tests**

Run: `dotnet test tests/PPDS.Query.Tests --no-restore --filter "Category!=Integration" -v q`
Run: `dotnet test tests/PPDS.Cli.Tests --no-restore --filter "Category!=Integration" -v q`

Expected: ALL PASS (except pre-existing failures in `ClearAllAsync_ClearsAllProfiles` and `ExportCsvAsync_WithNull_WritesEmpty` which are unrelated)

**Step 2: Final commit if any cleanup needed**
