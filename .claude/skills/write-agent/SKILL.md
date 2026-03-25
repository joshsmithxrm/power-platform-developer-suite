---
name: write-agent
description: Author or modify custom subagent definitions in .claude/agents/. Use when creating new agents, updating agent tool restrictions, or choosing model selection for specialized tasks.
---

# Writing Custom Subagents

Guide for authoring `.claude/agents/<name>.md` agent definitions.

## File Structure

```
.claude/agents/
  code-reviewer.md    # read-only reviewer (opus)
  fix-agent.md        # targeted fix agent (sonnet)
  explorer.md         # fast exploration (haiku)
```

## Frontmatter Reference

```yaml
---
name: agent-name           # kebab-case, action-oriented
model: opus|sonnet|haiku    # see Model Selection below
tools:                      # tool allowlist (restrict to minimum needed)
  - Read
  - Grep
  - Glob
  - Edit
  - Write
  - Bash(dotnet build:*)    # specific command patterns
  - WebSearch
memory: project|user|local  # persistent memory across sessions (optional)
effort: low|medium|high|max # reasoning depth override (optional)
---
```

## Model Selection Criteria

| Model | Cost | Speed | When to Use |
|-------|------|-------|-------------|
| **opus** | Highest | Slowest | Deep reasoning: code review, architecture decisions, complex debugging |
| **sonnet** | Medium | Balanced | Most tasks: implementing features, fixing bugs, writing tests |
| **haiku** | Lowest | Fastest | Mechanical tasks: codebase exploration, file search, evidence gathering |

**Default rule:** Use the cheapest model that can do the job. Upgrade only when the task requires deeper reasoning.

## Tool Restriction Patterns

### Read-only agent (reviewer, explorer)
```yaml
tools:
  - Read
  - Grep
  - Glob
  - Bash(git diff:*)
  - Bash(git log:*)
  - Bash(git show:*)
```

Prevents accidental edits. Use for review, audit, and research agents.

### Build-and-fix agent
```yaml
tools:
  - Read
  - Grep
  - Glob
  - Edit
  - Write
  - Bash(dotnet build:*)
  - Bash(dotnet test:*)
  - Bash(npm run:*)
  - Bash(git diff:*)
  - Bash(git status:*)
```

Can edit and verify via build/test. Use for fix agents, implementation agents.

### Full-access agent
Omit `tools` field entirely — agent gets all tools. Use sparingly, only when the task scope is unpredictable.

## When to Use `isolation: "worktree"`

Use worktree isolation when:
- Multiple agents work on the same codebase in parallel
- Agent changes might conflict with other in-progress work
- You want to review agent changes before merging

The worktree is auto-cleaned if the agent makes no changes. If changes are made, the worktree path and branch are returned.

## Memory Types

| Type | Scope | When to Use |
|------|-------|-------------|
| `project` | Shared across all sessions in this project | Agent learns project-specific patterns (reviewer learns common issues) |
| `user` | Shared across all projects for this user | Personal preferences, workflow habits |
| `local` | This machine only | Machine-specific paths, tool locations |

Memory is persistent — the agent accumulates knowledge across sessions. Use for agents that benefit from learning patterns (reviewers, explorers).

## Naming Conventions

- File: `{role}.md` or `{action}-{qualifier}.md` (kebab-case)
- Name field: matches filename without extension
- Description: written for AI discoverability (trigger words, not technology)

## Referencing Agents from Skills

In a skill or command, dispatch a custom agent:

```markdown
Dispatch a `code-reviewer` agent with the following context:
- The diff: `git diff main...HEAD`
- The constitution: `specs/CONSTITUTION.md`
- Relevant spec ACs
```

The Agent tool's `subagent_type` parameter selects the agent definition.
