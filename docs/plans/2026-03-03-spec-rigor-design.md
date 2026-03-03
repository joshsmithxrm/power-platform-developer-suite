# Spec Rigor Design

**Problem:** As the PPDS codebase grows, AI output is becoming inconsistent. Specs exist (21 specs, 11K lines) but nothing in the workflow forces them into the implementation or review pipeline. Subagents get plans pasted in but never read the specs. Reviews don't check against acceptance criteria. Fixes introduce regressions with no mechanical safety net.

**Evidence:** The vscode-extension-mvp worktree has gone through 5 review cycles. The latest review found 32 issues (13 critical) — 2.7x more than the first review. Biased review found 1 issue; independent review of the same code found 30. Top-churn files are all contract/protocol boundaries where spec clarity matters most.

**Solution:** Make specs the source of truth and wire them into every step of the workflow. Build skills that enforce spec discipline so the lazy path is also the rigorous path.

---

## Architecture

```
┌─────────────────────────────────────────────┐
│  PPDS Custom Skills (.claude/commands/)      │  ← Domain enforcement
│  /spec, /spec-audit, /implement, /debug      │     Version controlled, ours
│  Reads: CONSTITUTION.md + relevant specs     │
├─────────────────────────────────────────────┤
│  Superpowers Skills (plugin cache)           │  ← Generic workflow discipline
│  brainstorming, writing-plans, TDD,          │     Third-party, updates independently
│  subagent-driven-development, debugging      │
├─────────────────────────────────────────────┤
│  Claude Code (runtime)                       │  ← Orchestration
│  Skill routing, tool execution               │
└─────────────────────────────────────────────┘
```

Custom skills are standalone but superpowers-aware. They invoke superpowers skills when useful but own the enforcement layer. This avoids coupling to a third-party plugin's cache directory that updates independently.

---

## Deliverables

### 1. Constitution (`specs/CONSTITUTION.md`)

Non-negotiable principles that every skill reads before doing anything. Violations are defects.

```markdown
# PPDS Constitution

Non-negotiable principles. Every spec, plan, implementation,
and review MUST comply. Violations are defects.

## Architecture Laws

1. All business logic lives in Application Services — never in UI code
   (CLI commands, TUI screens, VS Code webviews, MCP handlers are thin wrappers)
2. Application Services are the single code path — CLI, TUI, RPC, MCP
   all call the same service methods
3. Accept IProgressReporter for operations >1 second — all UIs need feedback

## Dataverse Laws

4. Use IDataverseConnectionPool — never create ServiceClient per request
5. Never hold a pooled client across multiple operations — get, use, dispose
6. Use bulk APIs (CreateMultiple, UpdateMultiple) over ExecuteMultiple
7. Wrap exceptions in PpdsException with ErrorCode — enables programmatic handling

## Interface Laws

8. CLI stdout is for data only — status/progress goes to stderr
9. Generated entities (src/PPDS.Dataverse/Generated/) are never hand-edited
10. Every spec has numbered acceptance criteria before implementation begins

## Security Laws

11. Never render untrusted data via innerHTML — use textContent or proper escaping
12. Never use shell: true in process spawn without explicit justification
13. Never log secrets (clientSecret, password, certificatePassword, tokens)

## Resource Laws

14. Every IDisposable gets disposed — no fire-and-forget subscriptions
15. CancellationToken must be threaded through async call chains — never ignored
16. Event handlers and subscriptions are cleaned up in Dispose
```

Sources: CLAUDE.md rules, worktree review findings (XSS, dispose leaks, pool violations, shell:true, secret logging), existing spec patterns.

### 2. Sharpened Spec Template

Changes to `specs/SPEC-TEMPLATE.md`:

- `## Testing` section renamed to `## Acceptance Criteria` and promoted — moves right after `## Specification`, no longer buried at bottom
- Numbered IDs (`AC-01`) that are grep-able and referenceable
- Table format with Test column linking criterion to actual test method name
- Status column tracks coverage at a glance (✅ ⚠️ ❌)
- Edge cases and test examples sections stay as-is

```markdown
## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Pool returns client within 50ms under normal load | `PoolPerformanceTests.GetClient_UnderNormalLoad_ReturnsWithin50ms` | ✅ |
| AC-02 | Throttled client removed from rotation for backoff period | `ThrottleAwareStrategyTests.SelectConnection_SkipsThrottledConnection` | ✅ |
| AC-03 | Dispose returns connection to pool for reuse | `PoolLifecycleTests.GetClientAsync_ReturnsToPool_OnDispose` | ❌ |

Status: ✅ = covered by passing test, ⚠️ = test exists but failing, ❌ = no test yet
```

Why this matters: When `/implement` dispatches a subagent, it says "implement AC-01 through AC-05." When `/spec-audit` runs, it greps for referenced test methods and verifies they exist and pass. The convergence loop has concrete criteria — not "does this feel right" but "are AC-01 through AC-12 green."

### 3. `/spec` Skill (`.claude/commands/spec.md`)

Standardizes spec creation and updates.

**Invocation:** `/spec connection-pooling` (existing) or `/spec new-feature` (new)

**Workflow:**
1. Reads the constitution — always
2. Reads `SPEC-TEMPLATE.md` — for structure
3. Reads related existing specs — via `specs/README.md` dependency map
4. For existing specs: loads spec, compares against current code (reads files referenced in `Code:` header), identifies drift
5. For new specs: checks what exists to avoid duplication/contradiction, walks through template sections (one question at a time)
6. Enforces numbered ACs — refuses to finalize without them
7. Updates `specs/README.md` — keeps index current
8. Commits — `docs(specs): add/update {spec-name}`

Key behavior: cross-references related specs. Writing a TUI screen spec? It reads `specs/tui-foundation.md` to check lifecycle patterns. Touching Dataverse? It reads `specs/connection-pooling.md` to verify pool usage rules. Constitution violations are hard stops.

### 4. `/spec-audit` Skill (`.claude/commands/spec-audit.md`)

Repeatable comparison of specs against reality.

**Invocation:** `/spec-audit` (all specs) or `/spec-audit connection-pooling` (single)

**Workflow:**
1. Reads spec — focuses on Acceptance Criteria table and Core Requirements
2. Dispatches subagent per spec (parallel for full audit) that:
   - Reads code files referenced in spec header
   - Checks each AC: does referenced test exist? Does it pass?
   - Checks Core Requirements against actual implementation
   - Identifies undocumented behavior — code not covered by any spec
   - Identifies spec claims with no code — ACs referencing nonexistent tests
3. Produces audit report per spec with ✅/⚠️/❌ per AC, plus drift and gap findings
4. For full audit: summary with prioritized remediation list
5. Does NOT auto-fix — produces findings, then `/spec` updates each one

### 5. Spec-Aware `/implement` (updated `.claude/commands/implement.md`)

Injects spec context into the existing subagent dispatch pipeline. Additive, not a rewrite.

**What changes:**
1. Before dispatching subagents: maps plan files to specs via `specs/README.md`, loads constitution + relevant specs
2. Subagent prompt gets two new sections: Constitution (full text) + Relevant Specifications (AC tables from each relevant spec)
3. Spec-reviewer subagent gets same spec context — reviews against spec, not just plan
4. Phase gates check: do ACs referenced by this phase's tasks have passing tests?

**What doesn't change:** Overall orchestration, task granularity, commit patterns, worktree usage.

### 6. Convergence Skills

Build on the design in `2026-03-03-review-convergence-skills-design.md`. Spec rigor makes them more effective because they have concrete criteria to converge against.

**`automated-quality-gates` (`.claude/commands/automated-quality-gates.md`)**
- Runs compiler, linter, tests as mechanical pass/fail
- Addition: when specs with ACs are relevant, also runs specific AC test methods and reports which ACs are green/red

**`impartial-code-review` (`.claude/commands/impartial-code-review.md`)**
- Reviewer subagent gets constitution + spec ACs but NO implementation context
- Reviews code against specs independently — "does this code satisfy AC-03" not "did you follow the plan"

**`review-fix-converge` (`.claude/commands/review-fix-converge.md`)**
- Convergence criterion sharpens: done when gates clean + review finds 0 critical + all referenced ACs have passing tests
- AC table provides concrete definition of "done" independent of reviewer judgment

---

## Phased Execution

| Phase | Deliverable | Effort | Dependencies |
|-------|-------------|--------|--------------|
| 1 | Constitution + sharpened spec template | 1-2 hours | None |
| 2 | `/spec` skill | Half day | Phase 1 (needs template) |
| 3 | `/spec-audit` skill | Half day | Phase 1 (needs AC format) |
| 4 | Spec-aware `/implement` update | Half day | Phase 1 (needs constitution) |
| 5 | Convergence skills (gates, impartial review, convergence loop) | 1 day | Phase 1 (needs constitution + ACs) |
| 6 | Run `/spec-audit` across all 21 specs, remediate | Ongoing | Phases 1-3 |

Branch: `feature/spec-rigor` from `main`

## Design Decisions

### Why standalone skills, not superpowers extensions?

**Context:** Superpowers provides generic workflow skills (brainstorming, writing-plans, subagent-driven-development). PPDS needs domain-specific enforcement (constitution, specs, ACs).

**Decision:** Build standalone custom skills in `.claude/commands/` that invoke superpowers skills when appropriate but own the enforcement layer.

**Alternatives considered:**
- Modify superpowers skills directly: rejected because they live in plugin cache, overwritten on update, not version controlled
- Fork superpowers: rejected because it couples to their internal structure and requires tracking upstream changes

**Consequences:**
- Positive: version controlled, updateable independently, no coupling risk
- Negative: potential confusion about which skill to use (mitigated by clear naming and CLAUDE.md documentation)

### Why numbered ACs, not Gherkin/BDD?

**Context:** Need machine-parseable acceptance criteria that link specs to tests.

**Decision:** Simple `AC-01` numbered IDs in a markdown table with test method references.

**Alternatives considered:**
- Reqnroll/SpecFlow (Gherkin): rejected because it creates a second spec format alongside existing markdown specs, verbose for technical criteria
- Verify (snapshot testing): complementary but not a substitute for behavioral assertions
- ArchUnitNET: good for architectural constraints but doesn't cover behavioral acceptance criteria

**Consequences:**
- Positive: zero new tooling, grep-able, works with existing xUnit tests, incremental adoption
- Negative: no automatic enforcement that AC IDs are unique or well-formed (mitigated by `/spec` skill validation)

### Why branch from main, not vscode-extension-mvp?

**Context:** This work is cross-cutting infrastructure (specs, skills, workflow). The VS Code extension work is the evidence that motivated it, not a dependency.

**Decision:** Branch `feature/spec-rigor` from `main`. VS Code extension worktree picks it up by merging main.

**Consequences:**
- Positive: clean separation, available to all branches/worktrees immediately
- Negative: can't immediately test against the VS Code extension review findings (mitigated by Phase 6 audit)
