---
name: test-conventions
description: PPDS test conventions — which test framework to use per area, trait categories, file placement, coverage bar. Use when writing or organizing tests across .NET, TypeScript, or MCP code.
---

# Test Conventions

Where each kind of test lives, which framework it uses, and how to label it.

## When to Use

- Writing a new test class
- Deciding "should this be a unit test or a FakeXrmEasy test?"
- Looking up where to put a test file
- Adding the right trait so CI filters work

## Conventions Table

| Area | Test Type | Trait / Framework | Notes |
|------|-----------|-------------------|-------|
| Application Services | Unit (mocked deps) | `Unit` | Mock `IDataverseConnectionPool`, `IProgressReporter` |
| Dataverse SDK logic | FakeXrmEasy | `Unit` | Use `FakeXrmEasyTestsBase` for SDK behavior |
| Query engine | Unit (pure functions) | `Unit` | Deterministic transforms |
| Import orchestration | Unit + FakeXrmEasy | `Unit` | Mock pool, bulk executor |
| CLI commands | Unit (mock services) | `Unit` | Commands are thin wrappers — test services |
| TUI extracted logic | Unit | `TuiUnit` | Business logic, not Terminal.Gui rendering |
| Extension panels | Vitest | N/A | Message contracts + handler behavior |
| MCP tools | Unit (mock services) | `Unit` | Param validation + basic execution |
| Live Dataverse | Integration | `Integration` | Needs test-dataverse environment |

## File Placement

Mirror the source path under `tests/`:

```
src/PPDS.Cli/Services/AuthService.cs
tests/PPDS.Cli.Tests/Services/AuthServiceTests.cs
```

Project naming: `{SourceProject}.Tests` (unit) or `{SourceProject}.IntegrationTests`
(integration / live). Class naming: `{ClassUnderTest}Tests`.

## Coverage Bar

80% on new code (patch coverage), enforced by Codecov on PRs. Aim higher
in new modules; the bar is a minimum, not a target.

## AC Mapping

Every spec acceptance criterion (AC-NN) must have a corresponding test —
this is Constitution principle I6. Tag the test with `[Trait("AC", "AC-NN")]`
so the AC-coverage script can verify completeness.

## Running Tests

```bash
# Fast unit suite (no external deps, ~30s)
dotnet test PPDS.sln --filter "Category!=Integration" -v q

# TUI unit tests only
dotnet test --filter "Category=TuiUnit"

# Live Dataverse integration (requires test environment)
dotnet test PPDS.sln --filter "Category=Integration" -v q

# Extension unit (Vitest)
npm run ext:test

# Extension end-to-end (Playwright Electron)
npm run ext:test:e2e

# TUI snapshot suite (Node, captures terminal frames)
npm run tui:test
```

## Why This Lives in a Skill, Not CLAUDE.md

Test conventions are situational — they only matter when authoring tests.
Loading them into every session would crowd out genuinely-global context.
The skill auto-loads when you reach for tests; outside that context, it
sits idle.

See `docs/CLAUDE-MD-GOVERNANCE.md` for the routing rationale.
