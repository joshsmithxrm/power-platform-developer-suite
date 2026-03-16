---
name: write-skill
description: Author new skills following PPDS conventions — naming, structure, frontmatter, discoverability, workflow state integration. Use when creating or restructuring skills.
---

# Write Skill

Guide for authoring PPDS skills that are consistent, discoverable, and integrate with the workflow enforcement system.

## Naming Convention

**Pattern:** `{action}` or `{action}-{qualifier}`, kebab-case.

The name should describe what the skill does, not the technology it uses.

| Good | Bad | Why |
|------|-----|-----|
| `ext-verify` | `webview-cdp` | Users think "verify extension," not "use Chrome DevTools Protocol" |
| `tui-verify` | `terminal-pty` | Named by surface + action, not by technology |
| `ext-panels` | `webview-panels` | Consistent `ext-` prefix for extension skills |
| `design` | `brainstorm-spec` | Single word when unambiguous |
| `pr` | `create-pull-request` | Short is better when the action is obvious |
| `debug` | `systematic-debugging` | Verb, not adjective-noun |

**Qualifiers:** Only add when needed to disambiguate. `/verify` is the orchestrator. `/ext-verify` and `/tui-verify` are surface-specific because there are multiple surfaces.

## Directory Structure

Skills go in `.claude/skills/<name>/SKILL.md`:

```
.claude/skills/
  my-skill/
    SKILL.md           # Required — the skill content
    supporting-doc.md  # Optional — referenced by SKILL.md
    example.py         # Optional — code examples or scripts
```

Simple skills that need no supporting files can also be commands at `.claude/commands/<name>.md`. Both are invoked the same way with `/name`.

**When to use skills/ vs commands/:**
- Skill needs supporting files (reference docs, examples, scripts) → `skills/`
- Skill is a simple prompt with no supporting files → either works, prefer `skills/` for new work

## Frontmatter

Every SKILL.md starts with YAML frontmatter:

```yaml
---
name: my-skill
description: One sentence describing when to use this skill. Write for AI discoverability — this is what makes the AI auto-load the skill when the user says something relevant.
---
```

**Description tips:**
- Lead with the trigger condition: "Use when..." or "How to..."
- Include the words a user would say: "test the extension", "create a PR", "debug this"
- Don't describe the technology — describe the user's intent
- Keep under 200 characters

## Workflow State Integration

Skills that represent workflow steps should write to `.claude/workflow-state.json` on completion.

**When to write state:** Only if the skill represents a gate that hooks check (gates, verify, QA, review). Supporting knowledge skills (ext-verify, tui-verify, cli-verify, mcp-verify) do NOT write state — the orchestrator skill that invokes them does.

**How to write state:**

```
After successful completion, read `.claude/workflow-state.json` (create if missing),
update the relevant field with an ISO 8601 timestamp, and write the file back.

Example for /gates:
{
  "gates": {
    "passed": "2026-03-16T16:00:00Z",
    "commit_ref": "<current HEAD>"
  }
}
```

**State fields by skill:**

| Skill | Field | Value |
|-------|-------|-------|
| `/gates` | `gates.passed`, `gates.commit_ref` | ISO timestamp, HEAD SHA |
| `/verify` | `verify.{surface}` | ISO timestamp |
| `/qa` | `qa.{surface}` | ISO timestamp |
| `/review` | `review.passed`, `review.findings` | ISO timestamp, finding count |
| `/implement` | `branch`, `spec`, `plan`, `started` | strings, ISO timestamp |
| `/pr` | `pr.url`, `pr.created` | URL string, ISO timestamp |

## Skill Categories

For reference, PPDS skills fall into these categories:

| Category | Examples | User-invocable? |
|----------|----------|-----------------|
| **Workflow orchestration** | /design, /implement, /pr, /gates, /qa, /review, /converge | Yes |
| **Verification tools** | /verify, /shakedown | Yes |
| **Surface knowledge** | /ext-verify, /tui-verify, /mcp-verify, /cli-verify | AI-loaded |
| **Development guides** | /ext-panels, /write-skill | AI-loaded |
| **Analysis** | /retro, /debug, /status | Yes |

## Checklist

When creating a new skill:

1. Name follows `{action}` or `{action}-{qualifier}` convention
2. SKILL.md has frontmatter with `name` and `description`
3. Description is written for AI discoverability (trigger words, not technology)
4. If it represents a workflow gate, it writes to `workflow-state.json`
5. If it references other skills, it uses current names (not old names)
6. Directory is `.claude/skills/<name>/SKILL.md`
