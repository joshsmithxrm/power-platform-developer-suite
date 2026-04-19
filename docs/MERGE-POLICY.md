# Merge Policy

How PRs get merged into `main`. Operational reference for maintainers.

## Auto-Merge

Auto-merge is **enabled at the repo level** (`allow_auto_merge: true`). It does not bypass branch protection or skip required reviews — it only removes the "human clicks merge when CI goes green" step.

Enable auto-merge on a PR with:

```bash
gh pr merge <num> --squash --delete-branch --auto
```

### When to use auto-merge

Use auto-merge when **green CI is sufficient evidence to ship**. Specifically:

- Dependabot tooling/test bumps (eslint, knip, vitest, esbuild, @types/*, @playwright/test, Microsoft.NET.Test.Sdk)
- Dependabot patch-level bumps on non-critical paths
- GitHub Actions group bumps
- Lockfile-only changes
- Documentation-only PRs that have completed review
- Bot PRs targeting non-critical paths where CI fully exercises the change

### When NOT to use auto-merge

Do not use auto-merge when the diff requires human judgment beyond CI signal:

- Anything touching `src/PPDS.Auth/**` or crypto code paths
- Major version bumps on core libraries (Terminal.Gui, Microsoft.Identity.Client, Azure.Identity, Microsoft.PowerPlatform.Dataverse.Client)
- Plugin assembly changes that could interact with the strong-name signing key (`PPDS.Plugins.snk`)
- Changes to `Directory.Packages.props` that pin or unpin security-sensitive packages (e.g., `System.Security.Cryptography.Xml`)
- PRs with manual verification steps in their runbook (e.g., "spot-check `npm run package`", "run TUI snapshots locally")
- Release-branch changes
- Changes to CI/CD pipelines, branch protection rules, or release automation
- Anything where the reviewer wants to inspect runtime behavior, not just diffs

When unsure, default to manual merge. The cost of waiting is low; the cost of an unwanted merge is high.

## Grouped Dependabot PRs

Dependabot can roll multiple updates into a single grouped PR (titled `Bump the <group> group ...`). Member updates may span multiple update types (e.g., a group containing both patch bumps and a minor bump).

Classify a grouped PR by **most-conservative-wins** — the whole group inherits the most conservative classification of any member:

- Group contains **any major bump** -> Group C (manual review required)
- Group contains **any minor bump** (no majors) -> Group B (verify-then-merge)
- Group contains **only patch bumps** -> Group A (auto-merge eligible)

Reasoning: simplest to enforce, hardest to misinterpret, and consistent with the universal-exclusion-of-major-bumps decision from the v1-prelaunch retro. If a single risky member exists, the whole group gets the safer treatment — splitting the group to land safe members earlier is not worth the complexity.

The classifier in `scripts/dependabot/classify.py` implements this rule by parsing the dependabot PR body (which lists each member bump as `Updates <pkg> from <a> to <b>`) and selecting the most-conservative type seen.

## Branch Protection

`main` is protected. PRs must:

- Be up to date with `main` before merging (auto-merge handles this via dependabot rebases or `gh pr update-branch`)
- Pass all required status checks
- Have any required reviews approved

## Squash Policy

All merges to `main` use **squash merge** with branch deletion. Rebase merges are disabled. Use:

```bash
gh pr merge <num> --squash --delete-branch
```

This keeps `main` linear and gives one commit per PR for clean `git log` and `git bisect`.

## Dependabot Cadence

Dependabot config lives in `.github/dependabot.yml`. Bumps that meet the auto-merge criteria above can be `--auto` enabled in batch. Bumps that don't (auth, crypto, major core libs) get manual review with the verification steps documented in the relevant spec or commit message.

For roll-up branches consolidating many bumps into one PR: avoid unless auto-merge is unavailable. Per-bump squash commits give better `git bisect` granularity and clearer attribution than a single roll-up commit.
