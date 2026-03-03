# Spec Audit

Compare specifications against reality. Find drift, gaps, and undocumented behavior. Produces actionable findings — does not auto-fix.

## Input

$ARGUMENTS = spec name for single audit (e.g., `connection-pooling`), or empty for full audit of all specs

## Process

### Step 1: Load Foundation

Read these files:
- `specs/CONSTITUTION.md` — principles to check against
- `specs/README.md` — index of all specs and their code mappings

### Step 2: Determine Scope

**Single spec** (`$ARGUMENTS` provided):
- Read `specs/$ARGUMENTS.md`
- Proceed to Step 3 with this one spec

**Full audit** (`$ARGUMENTS` empty):
- Read `specs/README.md` to get list of all specs
- For each spec, dispatch a parallel subagent (use `Agent` tool with `subagent_type: "general-purpose"`) with the audit prompt below
- Collect all results and produce a summary

### Step 3: Audit a Single Spec

For each spec, check these categories:

**A. Acceptance Criteria Coverage**
- Does the spec have an `## Acceptance Criteria` section with numbered IDs?
- For each AC that references a test method: does that test file/method exist? (use Grep to search for the method name)
- For each AC with status marked passing: is the status accurate? (run the specific test if possible: `dotnet test --filter "FullyQualifiedName~{TestMethod}" -v q`)
- Report: verified, test exists but status wrong, test not found, or no AC section

**B. Code-to-Spec Alignment**
- Read the code files listed in the spec's `Code:` header
- Check Core Requirements: does each requirement have corresponding code?
- Check Architecture diagram: does the actual code structure match?
- Check Core Types: do the interfaces/classes described still exist with the same signatures?
- Report: matches, drifted (describe how), missing (spec claims but code doesn't have), or extra (code has but spec doesn't describe)

**C. Constitution Compliance**
- Check the spec's design against each relevant constitution principle
- Focus on: architecture laws (A1-A3) for any spec, Dataverse laws (D1-D4) for Dataverse specs, security laws (S1-S3) for UI specs
- Report: compliant or violation (cite principle and issue)

**D. Cross-Spec Consistency**
- Read related specs (from the `Related Specs` section)
- Check for contradictions between this spec and related ones
- Report: consistent or contradiction (describe)

### Step 4: Produce Report

**Single spec report format:**

```
## {spec-name}.md — Audit Report

**Last Updated:** {date from spec header}
**Code:** {code path from spec header}

### Acceptance Criteria
| ID | Criterion | Finding |
|----|-----------|---------|
| AC-01 | {criterion text} | verified: test exists and passes |
| AC-02 | {criterion text} | NOT FOUND: test method not found |
| — | No AC section | MISSING — needs AC table |

### Code Alignment
- {finding 1}
- {finding 2}

### Undocumented Behavior
- {code behavior not covered by any spec section}

### Constitution
- {compliant or violation with citation}

### Remediation Priority
1. {highest priority fix}
2. {next priority}
```

**Full audit summary format:**

```
## PPDS Spec Audit Summary — {date}

### Overview
| Spec | ACs | Alignment | Constitution | Priority |
|------|-----|-----------|--------------|----------|
| connection-pooling.md | 5/5 verified | 2 drifted | compliant | LOW |
| tui-foundation.md | no ACs | 3 missing | A1 violation | HIGH |

### High Priority Remediation
1. {spec}: {issue}
2. {spec}: {issue}

### Stats
- Specs with ACs: N/21
- Specs fully aligned: N/21
- Constitution violations: N
```

### Subagent Prompt (for parallel full audit)

When dispatching a subagent for a single spec during full audit, use this prompt:

```
You are auditing the PPDS specification at specs/{name}.md against the actual codebase.

Read specs/CONSTITUTION.md first for the principles to check against.

Your Job:
1. Read specs/{name}.md
2. Read the code files referenced in the spec's Code: header
3. Check each acceptance criterion — does the referenced test exist? Search with Grep.
4. Check core requirements — does the code match what the spec describes?
5. Check for undocumented behavior — code that does things the spec doesn't mention
6. Check constitution compliance

Report your findings using the single spec report format.

Do NOT fix anything. Just report findings.
```

## Rules

1. **Read-only** — this skill produces findings, never modifies code or specs
2. **Parallel for full audit** — dispatch one subagent per spec for throughput
3. **Evidence-based** — every finding must cite specific code or test references
4. **Actionable** — every finding includes what needs to change
5. **Prioritized** — HIGH = constitution violation or missing ACs, MEDIUM = drift, LOW = minor gaps
