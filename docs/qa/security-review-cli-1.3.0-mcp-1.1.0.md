# PPDS.Cli 1.3.0 / PPDS.Mcp 1.1.0 — Security Review

**Date:** 2026-07-14
**Reviewer:** Release-gate security review (agent-assisted, findings independently verified against source).
**Scope:** Delta `abd0d57f5..HEAD` (everything since the last stable release `Cli-v1.2.0` / `Mcp-v1.0.1`, both tagged 2026-06-17) restricted to `src/PPDS.Cli/` and `src/PPDS.Mcp/`.
**Method:** Full unified-diff review + read of the complete current source for every file-writing / path-handling / guardrail component, cross-checked with `git`. Focus areas: path traversal / zip-slip, arbitrary file deletion, new option path validation, secret logging, command injection, deserialization, MCP session-guardrail regression.

**Why this artifact exists:** The `/release` skill's stable-release security-review gate (Rule 8) requires a `docs/qa/security-review-*.md` covering the delta since the last stable tag. The prior artifact (`security-review-v1.md`, 2026-04-20) predates this delta and does not cover it, so this review was produced specifically for the 1.3.0 / 1.1.0 boundary.

---

## Summary

| Severity | Count |
|----------|-------|
| Critical | 0 |
| High     | 0 |
| Medium   | 0 |
| Low      | 1 |
| Info     | 2 |

**Verdict: SHIP.** No ship-blocker. The user-supplied-archive path has layered zip-slip protection, the net462 cache self-heal deletion is provably confined to a content-addressed, user-scoped path with no user-influenced traversal, and the plugin-registration changes strengthen rather than weaken existing guardrails. LOW-1 and the INFO items are non-blocking follow-ups.

---

## Focus-area results

### 1. Path traversal / zip-slip — PASS
User-supplied `.nupkg` (the only attacker-controllable archive) is extracted with `ZipFile.ExtractToDirectory(..., overwriteFiles: false)` (the .NET 8 built-in zip-slip mitigation) into a fresh per-invocation GUID temp directory, then **re-validated** by `AssertExtractedEntriesContained` (`NupkgExtractor.cs:163-197`): each entry is canonicalized via `Path.GetFullPath` and rejected unless it stays under the canonical destination (trailing-separator anchored, per-OS case comparison that only ever rejects). Verified directly in source.

The embedded net462 reference-assembly zip (`AssemblyExtractor.cs:217-221`) is a build-time trusted resource compiled into the CLI (`PPDS.Cli.csproj` embeds `Resources.Net462ReferenceAssemblies.zip`), not attacker input.

### 2. Arbitrary file deletion (net462 cache self-heal, #1326) — PASS
`TryDeleteDirectory` (`AssemblyExtractor.cs:286-297`) deletes only `Directory.Exists(directory)` targets, and the only production callers pass `cacheDir` / `stagingDir`, both derived as `Path.Combine(baseDir, <content-hash|.staging-GUID>)`. `baseDir` in production is `Path.Combine(Path.GetTempPath(), $"ppds-{userScope}", "net462-ref")` where `userScope` is `Environment.UserName` filtered through `Path.GetInvalidFileNameChars()` (which includes path separators on both Windows and Unix, so no traversal is expressible). The cache key `hash` is a SHA-256 of the embedded resource — fixed per CLI build, never user-influenced. `--reference-dir` does **not** flow into the cache root. Verified in source (`AssemblyExtractor.cs:165-297`).

### 3. `--reference-dir` new option — PASS
Declared with `.AcceptExistingOnly()` and re-guarded with `Directory.Exists` before `Directory.GetFiles(dir, "*.dll")`. Values are used only to enumerate DLLs fed to the metadata resolver — no shell, no `Process.Start`, no command-string construction, no logging of the values, and no influence on the delete path above.

### 4. Secret / credential logging — PASS
New stderr/error lines emit only assembly file names, exception `.Message`, and plugin message/entity names. No tokens, secrets, connection strings, or credential-store values are logged. MCP `--version` / `serverInfo.version` emit only the version string.

### 5. Command / argument injection — PASS
No `Process.Start`, shell invocation, or command-string construction anywhere in the delta.

### 6. Deserialization — PASS
Plugin assemblies are inspected with `MetadataLoadContext` (metadata-only; never executes assembly code or static initializers) — the correct safe API for untrusted DLLs. Archives use `System.IO.Compression`; no `BinaryFormatter`.

### 7. MCP session options / version reporting — PASS
`McpSessionOptions.IsVersionRequested` (`McpSessionOptions.cs:51-56`) is a pure `args.Contains("--version")` check. `Program.cs:14-18` short-circuits to a version print + `return 0` **before** any host build and before `Parse` runs. It does not touch, weaken, or bypass the `--read-only` / `--allowed-env` allowlists — `Parse` (`McpSessionOptions.cs:62-86`) is unchanged and only runs on the normal startup path. Verified in source.

### 8. Regression of existing guardrails — PASS (net positive)
`UpsertStepAsync` still calls `_guard.EnsureCanMutate(...)` before any write. The new message-filter guard (#1345) is a security improvement: it refuses to silently register an unfiltered **global** step (which would fire on every occurrence of a message) when an entity was specified but no filter resolved. Identity-based GUID targeting (`PluginStepIdentity`, #1302) removes the prior "update an arbitrary same-named row" hazard.

---

## Findings

### LOW-1 — Zip-slip re-validation runs after extraction, not before
`src/PPDS.Cli/Plugins/Extraction/NupkgExtractor.cs` (`ExtractToDirectory` then `AssertExtractedEntriesContained`)
The archive is written to disk first; the containment assertion inspects it only afterward. If the .NET 8 platform mitigation ever regressed, a traversal entry would already be written before the assertion threw. Impact is bounded — the primary .NET 8 mitigation is the real defense, and extraction targets a private per-invocation GUID temp dir — so this is defense-in-depth ordering, not a live vulnerability. Suggested fix: validate entry paths via `ZipFile.OpenRead` before extracting, so the guard is authoritative rather than post-hoc. **Non-blocking; track as backlog.**

### INFO-1 — CSV formula injection is pre-existing and unchanged
`src/PPDS.Cli/Infrastructure/Output/QueryResultFormatter.cs`
The delta only changes zero-row behavior and gates `Csv` to a small set of commands; `EscapeCsvField` is unchanged. CSV values beginning with `= + - @` are not neutralized against spreadsheet formula injection — but this is pre-existing behavior outside the delta's material change. Worth a separate backlog item, not a release gate.

### INFO-2 — `--reference-dir` DLL enumeration is trust-appropriate
No action needed. The option widens only where the user's own extraction reads DLLs, at the user's own privilege; no privilege boundary is crossed.

---

## Follow-ups (non-blocking)
- LOW-1: make the zip-slip guard pre-extraction authoritative in `NupkgExtractor`.
- INFO-1: neutralize CSV formula-injection prefixes in `QueryResultFormatter.EscapeCsvField`.
