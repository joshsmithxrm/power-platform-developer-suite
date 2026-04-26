# RPC Contracts

**Status:** Draft
**Last Updated:** 2026-04-25
**Code:** [tests/PPDS.Cli.DaemonTests/ProtocolContractTests.cs](../tests/PPDS.Cli.DaemonTests/ProtocolContractTests.cs), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/)
**Surfaces:** Extension

---

## Overview

Contract tests validate the RPC protocol between the VS Code Extension and the CLI daemon, preventing protocol drift. The daemon exposes 97 RPC methods via `[JsonRpcMethod]` attributes on `RpcMethodHandler`; the Extension calls these through `daemonClient.ts`. Without contract tests, changes to either side can silently break the integration.

### Goals

- **Method inventory**: A single test that reflects all `[JsonRpcMethod]` methods and asserts against a checked-in list, catching additions and removals
- **Response shape validation**: Targeted tests for high-value methods verifying response JSON properties and types match Extension expectations
- **Error contract validation**: Structured error responses (`RpcErrorData`) have consistent shapes across method categories

### Non-Goals

- Full coverage of all 97 methods in one pass (incremental coverage is the strategy)
- Semantic correctness of responses (that's integration testing, not contract testing)
- Bilateral TypeScript-side validation (the TypeScript client is checked indirectly via the inventory test catching daemon-side drift)
- Performance benchmarking of RPC methods

---

## Architecture

```
┌─────────────────────────┐     ┌─────────────────────────┐
│ Extension (TypeScript)  │     │ Daemon (C#)             │
│ daemonClient.ts         │────▶│ RpcMethodHandler.cs     │
│ - 63+ method calls      │     │ - 97 [JsonRpcMethod]s   │
└─────────────────────────┘     └─────────────────────────┘
         │                               │
         │                               │
         ▼                               ▼
┌─────────────────────────┐     ┌─────────────────────────┐
│ Method Inventory        │     │ Reflection Enumeration  │
│ (checked-in list)       │◀────│ (test-time discovery)   │
└─────────────────────────┘     └─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Shape Tests             │
│ (invoke method, assert  │
│  response properties)   │
└─────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Method inventory test | Reflects all `[JsonRpcMethod]` attributes, asserts against checked-in list |
| Shape tests | Invoke RPC methods via `DaemonTestFixture`, assert response JSON structure |
| Error contract tests | Verify error responses have `code`, `message`, and expected fields |
| `DaemonTestFixture` | Spawns daemon process, provides `JsonRpc` connection for tests |

### Dependencies

- Depends on: [authentication.md](./authentication.md) for auth/profile methods
- Depends on: [data-explorer.md](./data-explorer.md) for query methods including `query/validate`

---

## Specification

### Method Inventory Test

A single test discovers all RPC methods via reflection on `RpcMethodHandler` and asserts against a checked-in snapshot:

```csharp
[Fact]
public void AllRpcMethods_MatchCheckedInInventory()
{
    var methods = typeof(RpcMethodHandler)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .SelectMany(m => m.GetCustomAttributes<JsonRpcMethodAttribute>())
        .Select(a => a.Name)
        .OrderBy(n => n)
        .ToList();

    Assert.Equal(ExpectedMethods, methods);
}
```

When a method is added or removed, this test fails. The developer must explicitly update the inventory, making protocol changes visible in code review.

The checked-in inventory is a static `string[]` in the test class, not an external file. This keeps the contract co-located with the tests.

### Response Shape Tests

Each shape test invokes a method through `DaemonTestFixture` and asserts the response JSON has the expected properties. Tests use methods that work without a Dataverse connection (return empty results, use default state, or produce expected errors).

**Priority tiers for initial coverage:**

| Tier | Category | Methods | Rationale |
|------|----------|---------|-----------|
| 1 | Auth/Session | `auth/list`, `auth/who`, `auth/select` | Session lifecycle; every Extension panel depends on these |
| 1 | Environment | `env/list`, `env/who`, `env/select` | Environment context; required for all operations |
| 1 | Query | `query/sql`, `query/validate`, `query/complete` | Data Explorer critical path; ties into #650 |
| 2 | Solutions | `solutions/list`, `solutions/components` | Most-used Extension panel |
| 2 | Plugins | `plugins/list`, `plugins/get` | Plugin Registration panel |
| 2 | Plugin Traces | `pluginTraces/list`, `pluginTraces/get` | Plugin Traces panel |
| 3 | Metadata | `metadata/entities`, `metadata/entity` | Metadata Browser panel |
| 3 | Profiles | `profiles/create`, `profiles/delete` | Profile management |

Tier 1 is in-scope for this work. Tiers 2-3 are follow-up.

### Shape Assertion Pattern

Tests assert properties exist and have expected types, not specific values:

```csharp
[Fact]
public async Task QueryValidateResponse_HasExpectedShape()
{
    var result = await _fixture.Rpc.InvokeWithCancellationAsync<JObject>(
        "query/validate",
        new object[] { new { sql = "SELECT name FROM account", language = "sql" } },
        CancellationToken.None);

    Assert.NotNull(result);
    Assert.NotNull(result["diagnostics"]);
    Assert.Equal(JTokenType.Array, result["diagnostics"]!.Type);
}
```

For methods that return errors without a connection, test the error shape via `ErrorData` (StreamJsonRpc populates this from `LocalRpcException.ErrorData` as a `JsonElement`):

```csharp
[Fact]
public async Task QuerySql_WithoutConnection_ReturnsStructuredError()
{
    var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
        () => _fixture.Rpc.InvokeWithCancellationAsync<JObject>(
            "query/sql",
            new object[] { new { sql = "SELECT name FROM account" } },
            CancellationToken.None));

    var errorData = ex.ErrorData.Should().BeOfType<JsonElement>().Subject;
    errorData.TryGetProperty("code", out _).Should().BeTrue();
    errorData.TryGetProperty("message", out _).Should().BeTrue();
}
```

### Error Contract Tests

Verify consistent error response shapes across categories:

| Error Category | Expected Fields | Test Methods |
|----------------|----------------|--------------|
| Validation error | `code`, `message`, `validationErrors[].field`, `validationErrors[].message` | Methods with required params called without them |
| Auth error | `code`, `message`, `requiresReauthentication` | Methods requiring active session |
| Parse error | `code`, `message`, `diagnostics[]` | `query/sql` with invalid SQL |

### Constraints

- Tests run without a Dataverse connection — they validate protocol shape, not business logic
- Tests use `DaemonTestFixture` (existing shared fixture, one daemon per test run)
- Response assertions check property existence and type, not values
- The method inventory is a `string[]` in source, not an external file

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| RC-01 | Method inventory test discovers all `[JsonRpcMethod]` methods via reflection and asserts against checked-in list; adding or removing a method without updating the list causes a test failure | `ProtocolContractTests.AllRpcMethods_MatchCheckedInInventory` | ❌ |
| RC-02 | `auth/list` response shape test validates `profiles` array is present | `ProtocolContractTests.AuthListResponse_HasExpectedShape` | ✅ |
| RC-03 | `auth/who` error response includes structured `code` and `message` fields | `ProtocolContractTests.ErrorResponse_ContainsStructuredErrorCode` | ✅ |
| RC-04 | `env/list` response shape test validates `environments` array | `ProtocolContractTests.EnvListResponse_HasExpectedShape` | ❌ |
| RC-05 | `env/who` response/error has expected structure | `ProtocolContractTests.EnvWhoResponse_HasExpectedShape` | ❌ |
| RC-06 | `query/validate` response includes `diagnostics` array | `ProtocolContractTests.QueryValidateResponse_HasExpectedShape` | ❌ |
| RC-07 | `query/complete` response includes `items` array | `ProtocolContractTests.QueryCompleteResponse_HasExpectedShape` | ❌ |
| RC-08 | `query/sql` error response (no connection) has `code` and `message` | `ProtocolContractTests.QuerySqlError_HasExpectedShape` | ❌ |
| RC-09 | Validation errors return `validationErrors[]` array with `field` and `message` | `ProtocolContractTests.ValidationError_HasExpectedFormat` | ✅ |
| RC-10 | All Tier 1 methods (auth, env, query) have shape tests | Test class method count | ❌ |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Method added to daemon but not to inventory | Inventory test fails with clear diff showing the new method |
| Method removed from daemon | Inventory test fails with clear diff showing the missing method |
| Response gains a new property | Shape test still passes (additive changes are non-breaking) |
| Response loses an expected property | Shape test fails (breaking change detected) |
| Method called with no params when params required | Returns validation error with expected shape |
| Method called with wrong param types | Returns error with `code` and `message` |

---

## Design Decisions

### Why inventory test + targeted shape tests over full auto-generation?

**Context:** 97 RPC methods with 5% test coverage. Need a strategy to prevent protocol drift.

**Decision:** One reflection-based inventory test plus targeted shape tests for high-value methods, prioritized by Extension usage.

**Alternatives considered:**
- **Full auto-generated test harness**: Reflect all methods, auto-generate test stubs from parameter/return types. Too magical — generated tests are brittle, hard to debug when they fail, and don't verify meaningful response shapes.
- **Bilateral validation (parse TypeScript + reflect C#)**: Parse `daemonClient.ts` to find method calls, cross-reference with C# reflection. The TypeScript parsing is fragile and adds complexity without proportional value.
- **Contract manifest file (OpenAPI/JSON Schema)**: Generate a schema from C# types, validate both sides against it. Over-engineered for an internal protocol — the inventory test achieves the same goal for method names, and shape tests are more readable than schema comparisons.

**Consequences:**
- Positive: High leverage (one test catches all 97 method additions/removals), incremental growth path for shape tests, tests are readable and debuggable
- Negative: Doesn't auto-detect Extension-side drift (acceptable — Extension changes go through PR review)

### Why checked-in string array over external file?

**Context:** The method inventory needs to be stored somewhere for comparison.

**Decision:** Static `string[]` in the test class.

**Rationale:** Co-locates the contract with the tests. External files (JSON, YAML) add indirection and tooling. A diff in the test file is immediately visible in code review. The list is ~100 entries — fits comfortably in a source file.

---

## Related Specs

- [data-explorer.md](./data-explorer.md) — `query/validate` endpoint adds a new contract to test
- [authentication.md](./authentication.md) — Auth RPC methods covered in Tier 1

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-25 | Initial spec (#672) |
