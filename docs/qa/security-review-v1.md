# PPDS v1.0.0 — Security Review

**Reviewer role:** W2 (security) of a 3-agent parallel v1 review.
**Scope:** v1.0.0 delta — all `src/`, `.claude/hooks/`, `.github/workflows/`, vendored third-party code, dependency tree.
**Method:** Source-code review + `dotnet list package --vulnerable --include-transitive`, `npm audit --production`, git history mining for secrets/keys, static inspection of hook/webview trust boundaries.

---

## Summary

| Severity  | Count |
|-----------|-------|
| Critical  | 0     |
| High      | 1     |
| Medium    | 2     |
| Low       | 3     |
| Info      | 4     |

**Verdict: SHIP WITH FOLLOW-UPS.** No ship-blocker. One High-severity transitive CVE (`System.Security.Cryptography.Xml 8.0.2`) has a clean upgrade path and would be rolled in the next patch. Everything else is hygiene / defence-in-depth.

Highlights:

- `PPDS.Auth` credential handling is **substantially better than typical** for a multi-platform tool: DPAPI on Windows, Keychain on macOS, libsecret on Linux, with a sharp double-gated plaintext fallback (env var + opt-in flag) that is clearly marked and only relevant for CI.
- Strong-name private keys that were leaked into git history in pre-v1 commits **have been rotated** (verified by comparing derived `PublicKeyToken`s).
- Extension webviews uniformly use `escapeHtml`/`escapeAttr` around `innerHTML` assignments, and CSP is `default-src 'none'` with nonce-scoped scripts. No DOM-based XSS found.
- MCP server's `--read-only` and `--allowed-env` allowlists are enforced server-side in `McpToolContext`, not only advisory in a tool description.

---

## Criticals

None.

---

## By area

### 1. Credential handling

Files reviewed: `src/PPDS.Auth/Credentials/*.cs`, `src/PPDS.Auth/Internal/CredentialStore/**`, `src/PPDS.Auth/Profiles/ProfileEncryption.cs`, `src/PPDS.Auth/EnvironmentVariableAuth.cs`.

**Findings:**

- **Vendored git-credential-manager (PR #803) — clean.** 23 `.cs` files were vendored into `src/PPDS.Auth/Internal/CredentialStore/`: 3 per-OS credential-store facades (`Windows/WindowsCredentialManager.cs`, `MacOS/MacOSKeychain.cs`, `Linux/SecretServiceCollection.cs`) plus a PPDS-authored `Linux/PlaintextCredentialStore.cs` fallback, and 19 supporting types — native P/Invoke wrappers (`Windows/Native/Advapi32.cs`, `Windows/Native/Win32Error.cs`, `MacOS/Native/CoreFoundation.cs`, `MacOS/Native/SecurityFramework.cs`, `MacOS/Native/LibSystem.cs`, `Linux/Native/Libsecret.cs`, `Linux/Native/Glib.cs`, `Linux/Native/Gobject.cs`), per-OS credential value types (`Windows/WindowsCredential.cs`, `MacOS/MacOSKeychainCredential.cs`, `Linux/SecretServiceCredential.cs`), and shared facade/helper types (`CredentialManager.cs`, `ICredential.cs`, `ICredentialStore.cs`, `EnsureArgument.cs`, `InteropException.cs`, `InteropUtils.cs`, `PlatformUtils.cs`, `StringExtensions.cs`). Each file carries an explicit "Vendored from https://github.com/git-ecosystem/git-credential-manager (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a)" header, MIT attribution, and documented deltas (internals lowered, GCM-internal `ITrace/IFileSystem` abstractions dropped). `THIRD_PARTY_NOTICES.md` contains the v2.7.3 + commit SHA. Upstream commit is genuine (public, signed tag). No private keys or credentials copied. Existing `DependencyAuditTests.PpdsAuthAssembly_DoesNotReferenceDevlooped` test enforces that the replaced `Devlooped.*` dependency is gone (AC-13). **PASS.**
- **DPAPI (Windows) — correct use.** `ProfileEncryption.Encrypt` uses `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`. `NativeCredentialStore` writes through `WindowsCredentialManager` using `CredWrite` with `CredentialType.Generic` and `CredentialPersist.LocalMachine` (machine-local, user-scoped). No plaintext persistence of refresh tokens in profile JSON. **PASS.**
- **macOS Keychain — correct use.** `MacOSKeychain.AddOrUpdate` uses `SecKeychainAddGenericPassword` / `SecKeychainItemModifyAttributesAndData`. Data is UTF-8 bytes. No `kSecAttrAccessible` set explicitly — inherits default (`kSecAttrAccessibleWhenUnlocked`). Acceptable; tightening to `AfterFirstUnlockThisDeviceOnly` would be marginal hardening only. **PASS.**
- **Linux Secret Service — correct; plaintext fallback double-gated.** `CredentialManager.Create` requires **both** `allowPlaintextFallback=true` **and** `GCM_CREDENTIAL_STORE=plaintext` env before activating `PlaintextCredentialStore`. The fallback writes `0600` files under `~/.gcm/store/` and clamps `0700` on the parent directory (explicit `File.SetUnixFileMode` calls, which guard against inherited `umask 0022`). **PASS.**
- **MSAL token cache.** `MsalClientBuilder.CreatePlatformCacheHelperAsync` prefers libsecret on Linux with explicit `WithLinuxKeyring(...)` and falls back to `WithUnprotectedFile()` only when libsecret is absent, with a user-facing warning. Windows/macOS use MSAL defaults (DPAPI / Keychain). The `WithUnprotectedFile` fallback on Linux is the **only** place where an MSAL cache can land on disk in cleartext. This is documented and warned about. **(Low) Finding 1 below.**
- **Auth mode enumeration:**
  - **Interactive browser / device code** — MSAL public client, cached through MSAL. No issues.
  - **Client secret** — retrieved from `NativeCredentialStore` by `ApplicationId` key. `PPDS_SPN_SECRET` env var override has documented precedence over store lookup. No secret material in `AuthProfile` (moved out in beta.6 per changelog).
  - **Certificate file / store** — `CertificateFileCredentialProvider` accepts a password from the credential store only. No password persistence in the profile JSON.
  - **Managed identity** — uses `Azure.Identity` `ManagedIdentityId.SystemAssigned` / `FromUserAssignedClientId`.
  - **OIDC federated (GitHub / Azure DevOps)** — `GitHubFederatedCredentialProvider`, `AzureDevOpsFederatedCredentialProvider` receive OIDC assertions from environment variables. Verified the assertion is **not** logged via `AuthDebugLog` (grep of provider files; debug lines log only `url`, `expiresOn`, `resource`, exception messages).
  - **Username/password** — requires credential store; `CredentialProviderFactory.Create` (sync) explicitly refuses it; `CreateAsync` pulls password from `NativeCredentialStore` keyed by username. No password in profile JSON.
- **`EnvironmentVariableAuth` (PR #706 series, v1 final).** `TryCreateProfile` requires all four `PPDS_*` vars; partial configuration throws `AuthenticationException("Auth.IncompleteEnvironmentConfig")` rather than silently degrading. URL must be `https://`. No logging of the secret. **PASS.**
- **Pre-release non-Windows XOR scheme (old `ENCRYPTED:` prefix).** `ProfileEncryption.Decrypt` now throws `AuthenticationException("Auth.LegacyEncryptedProfileUnsupported")` on non-Windows, per CHANGELOG Unreleased entry, forcing reauth instead of silently returning empty (which previously cascaded into "wrong credentials" UX). **PASS.**

### 2. Secret redaction

- **`PPDS.Auth/SensitiveValueRedactor`** and **`PPDS.Dataverse/Security/ConnectionStringRedactor`** are intentionally duplicated (documented in the Auth copy's XML remark) so `PPDS.Auth` can stay free of a Dataverse dependency. Both redact keys: `ClientSecret|Password|Secret|Key|Pwd|Token|ApiKey|AccessToken|RefreshToken|SharedAccessKey|AccountKey|Credential`. Regex captures both bare and quoted forms. **(Info) Finding below.**
- **`ppds logs dump`** (`LogsDumpCommand`) streams each `~/.ppds/*.log` line-by-line through `ConnectionStringRedactor.RedactExceptionMessage` *before* the ZIP is written, redacts the value of any `PPDS_*` env var whose **name** matches `secret|password|pwd|token|apikey|key|credential|cert|clientsecret`, and warns the user that customer data in log *messages* may still be present. **PASS.**
- **AuthDebugLog.** All 77 `AuthDebugLog.WriteLine` call sites were inspected; none format secret/password/token/assertion values. Typical lines include URL, resource, expiry time, `ex.Message` — none of which carry credentials unless a caller is already broken. **PASS.**
- **CI workflows.** `.github/workflows/*` use `${{ secrets.X }}` only in `env:` blocks (consumed by child processes), never echoed. No `set -x` in any workflow. `PPDS_DOCS_APP_PRIVATE_KEY` (PEM) is used via the `actions/create-github-app-token@v1` action (no manual parsing). **PASS.**
- **Hook stdout/stderr.** `pr-gate.py`, `shakedown-safety.py`, `stop-hook-watchdog.py` read `tool_input.command` from stdin but do not echo the full command back; error messages are hand-crafted strings. No hook was seen emitting raw stdin content to telemetry. **PASS.**

### 3. Trust-boundary input validation

- **CLI args (`src/PPDS.Cli/Commands/**`)** — all options wired through `System.CommandLine` `Option<T>` with `.Validators.Add(...)` for ranges. `ExportCommand` validates `--output` directory exists before writing; path traversal is possible via relative `--output` but limited to user's own filesystem (no elevated exec). No format-string sinks (no `Console.WriteLine(userInput)` anywhere — all user strings are interpolated as plain arguments). **PASS.**
- **`ppds query sql`** — SQL is parsed by `PPDS.Query` (ScriptDom-backed). Cross-environment `[env].[entity]` syntax is resolved via `PPDS.Query` identifier parsing, not string splicing. Output path is the standard `--output` `FileInfo` option. **PASS.**
- **MCP tool inputs (`src/PPDS.Mcp/Tools/**`)** — every tool declares `[Description]` on each parameter and uses strongly-typed C# parameter signatures (e.g., `ExecuteAsync(string schemaName, string value, ...)`). The MCP host framework (ModelContextProtocol library) generates a JSON schema from these signatures; no raw `JsonElement` is consumed. `EnvironmentVariablesSetTool` short-circuits with `InvalidOperationException` when `Context.IsReadOnly`. `EnvSelectTool` calls `Context.ValidateEnvironmentSwitch(resolved.Url)` which enforces the `--allowed-env` allowlist **server-side** before updating profile state. **PASS.**
- **Hook shell usage.** Only `pre-commit-validate.py` uses `shell=True`, and its command is a **literal** string (`"npm run lint"`), required on Windows for `.cmd` resolution. No user-controlled interpolation. All other hooks use `subprocess.run([...])` with list args. No `eval` / `exec` / `os.system` anywhere in `.claude/hooks/`. **PASS.**
- **Extension webview `postMessage`.**
  - `WebviewPanelBase.initPanel` wraps `handleMessage` in try/catch and awaits the promise tail.
  - All 19+ `.innerHTML =` sites in webview files are paired with `escapeHtml`/`escapeAttr` from `dom-utils.ts` for any untrusted substring. A spot-check of `virtualScrollScript.ts:100`, `query-panel.ts:943`, `connection-references-panel.ts:389`, `plugins-panel.ts:1913` (annotated "Safe: we control all content via createElement") confirms the pattern.
  - CSP in every panel matches `default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource}; worker-src blob:;`. `'unsafe-inline'` on `style-src` is a standard VS Code webview concession (the VS Code Toolkit injects inline styles) and is not exploitable without an already-compromised script; scripts are nonce-gated. **PASS** (with minor note: `style-src 'unsafe-inline'` is a low-severity trade-off accepted by VS Code's own webview guide).

### 4. Dependency CVEs

| Project | Package | Resolved | Severity | CVE | Fixed in |
|---|---|---|---|---|---|
| PPDS.Dataverse, PPDS.Migration, PPDS.Query (all tfms: net8/9/10) | System.Security.Cryptography.Xml (transitive) | 8.0.2 | **High** | GHSA-37gx-xxp4-5rgx | 8.0.3 / 9.0.0+ |
| same | same | 8.0.2 | **High** | GHSA-w3x6-4m5h-cxqf | 8.0.3 / 9.0.0+ |

**Source of the transitive:** this is pulled in by one of the Dataverse SDK / Azure.Identity dependency chains. `PPDS.Auth` itself is clean (`no vulnerable packages`). A central pin in `Directory.Packages.props` to `System.Security.Cryptography.Xml >= 10.0.5` (matching the already-bumped `System.Security.Cryptography.Pkcs 10.0.5` and `Microsoft.Bcl.Cryptography 10.0.5`) resolves both advisories with no behavioral risk. **Not a ship-blocker** (the library is not on any signed-XML code path in PPDS — the CVEs are XML-DSig signature-verification bypasses that matter when you *accept* externally-signed XML, which PPDS does not), but should be rolled in a patch release.

**Extension `npm audit --production`:** 0 vulnerabilities across 13 prod deps. **PASS.**

### 5. Strong-name rotation

- **No `.snk` files present in the working tree.** `PPDS.Plugins.PublicKey` (160 bytes, `RSA1` public-key blob) is the only signing material on disk. `PublicSign=true` in `PPDS.Plugins.csproj`; the real private key is injected at release time from `PLUGINS_SNK_BASE64` (see csproj comment — "NEVER commit a real .snk private key").
- **History mining.** Three `.snk` files (1172, 596, 596 bytes) are reachable via `git log --all -- '*.snk'` in commits `66a8b15d8` and `2a5b2141f` (pre-v1 work). `PPDS.Plugins.snk @ 66a8b15d8` is a full `RSA2` 2048-bit private-key blob (PRIVATEKEYBLOB, bType=0x07). `PPDS.Dataverse.snk` / `PPDS.Migration.snk` at 596 bytes are 1024-bit private-key blobs.
- **Rotation verified.** The `PublicKeyToken` derived from the *leaked* `PPDS.Plugins.snk` (`87a4b0dac59374c6`) **does not match** the current `PPDS.Plugins.PublicKey` token (`0b0809faff135778`). The `PPDS.Dataverse` and `PPDS.Migration` assemblies are **no longer strong-signed at all** (no `SignAssembly` in their csprojs), so those leaked keys can't impersonate a current signing identity either. Rotation occurred in PR #792 as expected. **PASS.**
- **(Medium) Finding 2.** The leaked private keys remain in git history — while they can no longer sign as the current identity, third parties could use them to sign anything they like and market it as "PPDS v0.7.x"-era. Acceptable for a project with no pre-v1 production users, but worth noting in SECURITY.md that any pre-1.0.0 signed artefacts are not to be trusted.

### 6. New hook behaviors (auth-critical)

- **`pr-gate.py` (PR #845).** Enforces agent-context PR creation via `/pr` skill. Reads `tool_input.command` from stdin; uses `subprocess.run([list])` for all git operations; no shell interpolation. No stdin content echoed to logs. Correct parent-envelope key (`tool_input.command`, fixed in PR #816). **PASS.**
- **`stop-hook-watchdog.py` (PR #837).** Tracks per-`(session, hook)` Stop firings with atomic O_EXCL lock + temp-file replace. Fails open on lock timeout (documented). State file under `.claude/state/hook-counts.json` contains only session IDs, hook names, and timestamps — no tool input. **PASS.**
- **`pre-commit-validate.py` (PR #841 dedupe).** Uses `shell=True` once with a literal string; no interpolation. **PASS.**
- **Hook trust surface.** Hooks execute under Claude's permissions — but none spawns a shell against user-controlled data. Checked: `checkout-guard.py`, `claudemd-line-cap.py`, `notify.py`, `post-commit-state.py`, `protect-main-branch.py`, `review-guard.py`, `rm-guard.py`, `session-start-workflow.py`, `session-stop-workflow.py`, `shakedown-readonly-guard.py`, `shakedown-safety.py`, `snk-protect.py`. **PASS.**

### 7. Shakedown env allowlist + write-block (PR #822)

- **Scope clarification:** `shakedown-safety.py` is a **dev-time Claude Code hook**, not a runtime CLI gate shipped to users. It governs what the in-repo agent workflow is allowed to do.
- **Allowlist sources (checked in order):** `$PPDS_SAFE_ENVS` env var → `.claude/settings.json` `safety.shakedown_safe_envs` array → legacy `.claude/state/safe-envs.json` (explicitly marked "one-release fallback"). **Default-deny** when none set: `BLOCK with an instructive message`. **PASS.**
- **Write-block mode.** Activated by `PPDS_SHAKEDOWN=1`. Refuses `create|update|delete|plugins deploy` (without `--dry-run`) and `solutions import`. Only bypass is unsetting the env var — intentionally no softer escape hatch (good). **PASS.**
- **Fail-safe:** unknown active env → BLOCK (explicitly preferred over "allow on unclear state"). **PASS.**

### 8. Pre-merge gate secret-refs check (PR #821)

- **Regex:** `SECRET_REF_RE = r"\$\{\{\s*secrets\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}"`. Matches all valid GitHub Actions secret-reference forms; does not match inside comments — which is fine because GH Actions substitutes before YAML semantics (same note in the code). Case-normalized to uppercase before comparing against `gh secret list` output (also case-insensitive in GH). **PASS.**
- **Bypass marker:** `[secret-ref-allow: NAME]` in PR title or body, repeated for each legitimate missing name (e.g., reusable-workflow-defined secrets). Clear escape hatch without being a blanket "skip rule". **PASS.**
- **Permissions:** uses a dedicated GitHub App (`PPDS_GATE_APP_ID` / `PPDS_GATE_APP_PRIVATE_KEY`) for the `secret-refs` job so the default `GITHUB_TOKEN` is not over-privileged. App scope is `secrets:read + variables:read` only (names, not values). **PASS.**
- **Deleted workflow files** are excluded from scanning (bug mode avoided: ENOENT on tree checkout when a file was removed in the PR). **PASS.**

---

## Findings list (numbered)

**(High) 1. Transitive CVE in `System.Security.Cryptography.Xml 8.0.2`**
Two GHSA advisories (GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf) pulled in via Dataverse SDK / Azure.Identity chain across PPDS.Dataverse, PPDS.Migration, PPDS.Query on all three tfms. **Not exploitable in PPDS** (no signed-XML acceptance code path), but should be closed in the next patch release by pinning `System.Security.Cryptography.Xml >= 10.0.5` in `Directory.Packages.props`.

**(Medium) 2. Legacy strong-name private keys remain in git history**
`PPDS.Plugins.snk` (RSA2 2048-bit) and `PPDS.Dataverse.snk` / `PPDS.Migration.snk` (RSA1 1024-bit) are reachable at commits `66a8b15d8` and `2a5b2141f`. Rotation verified (current `PublicKeyToken` differs), so they cannot sign as the current identity. However, any pre-1.0.0 assembly that still trusts those tokens would be vulnerable. Recommend adding a SECURITY.md note that pre-v1 signed artefacts are out of support.

**(Medium) 3. Linux MSAL token cache may fall back to unprotected file**
When libsecret is absent on Linux, `MsalClientBuilder.CreatePlatformCacheHelperAsync` silently degrades to `WithUnprotectedFile()` with a `Console.Error` warning. Acceptable for headless CI, but the warning currently says "unprotected on disk" without clamping file permissions. Consider clamping the MSAL cache file to `0600` explicitly after first write as defence-in-depth (MSAL's default may already be user-only on many distros, but is not contractually guaranteed).

**(Low) 4. `style-src 'unsafe-inline'` in extension CSP**
Standard VS Code webview concession for the Toolkit's injected styles. Not exploitable without already having script execution. Future hardening could move to `'unsafe-hashes'` with precomputed inline-style hashes if VS Code ever documents them.

**(Low) 5. macOS Keychain items use default `kSecAttrAccessible`**
`MacOSKeychain.AddOrUpdate` does not set `kSecAttrAccessible`, so items inherit `kSecAttrAccessibleWhenUnlocked` (the default). Tightening to `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` would prevent credential items from being backed up off-device. Minor hardening; the current behaviour matches upstream `git-credential-manager`.

**(Low) 6. `SensitiveValueRedactor` regex in `PPDS.Auth` is duplicated from `PPDS.Dataverse`**
Documented in the XML remark; intentional to avoid a cross-assembly dependency. Risk is drift: future changes to `ConnectionStringRedactor.SensitiveKeys` may not reach the Auth copy. Worth an analyzer or a unit test that asserts the two `SensitiveKeys` lists are equal.

**(Info) 7.** `AuthDebugLog.Writer` defaults to `null` (no output) and is opt-in via `TuiDebugLog` redirect. Good design — no risk of accidental token leakage from a misconfigured logger.

**(Info) 8.** MCP server's `Console.SetOut(Console.Error)` *at process start* is the canonical correct pattern for stdio MCP transports. Prevents any future `Console.WriteLine` from corrupting the protocol stream.

**(Info) 9.** `EnvironmentVariableAuth` requires HTTPS URL scheme and rejects partial `PPDS_*` configurations with a typed exception — fail-closed on misconfiguration rather than silently attempting auth with wrong creds.

**(Info) 10.** `CredentialProviderFactory.ShouldBypassCredentialStore()` is a convenience that returns true when `PPDS_SPN_SECRET` is present. Documented behaviour; the env-var name is stable public API.

---

## Proposed follow-ups (for user approval)

1. **Pin `System.Security.Cryptography.Xml` to `>= 10.0.5` in `Directory.Packages.props`** to close GHSA-37gx-xxp4-5rgx / GHSA-w3x6-4m5h-cxqf. Target: 1.0.1 patch.
2. **Add a SECURITY.md note** that pre-1.0.0 strong-signed PPDS.Plugins/Dataverse/Migration assemblies (PublicKeyTokens `87a4b0dac59374c6` and legacy Dataverse/Migration tokens) are not to be trusted, and that the leaked keys in git history remain historically visible but cannot impersonate the current signing identity.
3. **Clamp MSAL `WithUnprotectedFile` Linux fallback to `0600`** via `File.SetUnixFileMode` post-creation, matching `PlaintextCredentialStore`'s behaviour. Defence in depth against inherited `umask 0022`.
4. **Add a unit test** pinning `SensitiveValueRedactor.SensitiveKeys` (Auth) equal to `ConnectionStringRedactor.SensitiveKeys` (Dataverse) — prevents silent drift between the two redaction surfaces.
5. **Consider `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`** on macOS keychain writes. Low-priority hardening that matches Apple's current guidance for background-accessible credential items; deviates from upstream GCM behaviour so coordinate with vendored-code policy first.
