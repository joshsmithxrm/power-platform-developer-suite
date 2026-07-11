---
name: write-skill
description: Author or modify skills following PPDS conventions — naming, structure, frontmatter, discoverability. Use when creating, editing, or restructuring skills.
---

# Write Skill

Guide for authoring PPDS skills that are consistent and discoverable.

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

**Qualifiers:** Only add when needed to disambiguate. `/verify` is the orchestrator; surface-specific guides are in `/verify` `REFERENCE.md` sections.

## Directory Structure

Skills go in `.claude/skills/<name>/SKILL.md`:

```
.claude/skills/
  my-skill/
    SKILL.md           # Required — the skill content
    supporting-doc.md  # Optional — referenced by SKILL.md
    example.py         # Optional — code examples or scripts
```

All skills live in `.claude/skills/{name}/SKILL.md` with YAML frontmatter. The `.claude/commands/` directory has been deprecated and removed.

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

## Two-File Pattern

SKILL.md is capped at 150 lines and is the entry point loaded on demand. Move rationale, worked examples, and long tables into a sibling `REFERENCE.md` that SKILL.md references. See `.claude/skills/TWO-FILE-PATTERN.md`.

## Authoring an Agent

Agents live in `.claude/agents/<name>.md` with YAML frontmatter (`name`, `description`, `tools`, optional `model`). The same discoverability rules apply to the `description`: lead with when to dispatch the agent and the words that should trigger it. Keep the `tools` list minimal — grant only what the agent needs.

## Skill Categories

For reference, PPDS skills fall into these categories:

| Category | Examples | User-invocable? |
|----------|----------|-----------------|
| **Workflow orchestration** | /design, /implement, /pr, /gates, /qa, /review | Yes |
| **Verification tools** | /verify, /shakedown | Yes |
| **Surface knowledge** | /verify (§ext, §tui, §mcp, §cli in REFERENCE.md) | AI-loaded |
| **Development guides** | /ext-panels, /write-skill | AI-loaded |
| **Analysis** | /debug | Yes |

## Checklist

When creating a new skill:

1. Name follows `{action}` or `{action}-{qualifier}` convention
2. SKILL.md has frontmatter with `name` and `description`
3. Description is written for AI discoverability (trigger words, not technology)
4. If it references other skills, it uses current names (not old names)
5. Directory is `.claude/skills/<name>/SKILL.md`
6. SKILL.md is at or under 150 lines; rationale lives in REFERENCE.md
