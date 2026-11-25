# Versioning

How PPDS packages are versioned, what counts as a breaking change, and how changes are announced.

## 1. Packages and versions

Each package has its own version line and its own tag prefix. Versions follow Semantic Versioning 2.0.0, applied to the contract in section 2.

| Package | Tag prefix | Notes |
|---|---|---|
| PPDS.Cli (CLI, TUI, daemon) | `Cli-v` | |
| PPDS.Mcp | `Mcp-v` | |
| VS Code extension | `Extension-v` | Bundles exactly one CLI |
| PPDS.Query | `Query-v` | |
| PPDS.Dataverse | `Dataverse-v` | |
| PPDS.Migration | `Migration-v` | Follows a PPDS.Dataverse major |
| PPDS.Auth | `Auth-v` | |
| PPDS.Plugins | `Plugins-v` | Strong-named; its assembly identity changes only with its major |

1.1 A package takes a new major version when its own contract breaks. Packages do not change major together unless section 1.2 requires it.

1.2 PPDS.Query and PPDS.Migration expose PPDS.Dataverse types. When PPDS.Dataverse takes a major, they take a major in the same release.

1.3 A version number that has been tagged is never used again, whether or not it was published.

1.4 A suite release is named after the extension version (for example "PPDS 2.0") and has a unified tag `vX.Y.Z` on the same commit as every package tag in it. The release notes for a suite release list the version of every package.

1.5 Odd and even minor versions select the Marketplace channel for the extension only: an odd minor is a pre-release, an even minor is stable. No other package gives the minor's parity a meaning; pre-releases of the other packages carry a pre-release label.

1.6 A stable tag may share a commit with its own release candidate tags: the stable release is the last candidate that passed.

## 2. What the contract is

| Surface | Contract | Not contract |
|---|---|---|
| CLI | Command paths; long option names; short aliases; exit codes and their meanings; for JSON output: the envelope, field names and types, and error codes | Wording of messages; the layout of text output; anything on stderr other than the error code; TUI layout and key bindings |
| MCP server | Tool names; input schemas; result field names and types; error codes; server arguments | Tool descriptions |
| Extension | Command identifiers; settings keys; the notebook file format; the URI scheme of its editors | Panel layout |
| Daemon protocol | None. It is private to an extension and the CLI it bundles, and carries a protocol version that both sides check | |
| Libraries | The public API recorded in each package's API baseline files | Anything not in the baseline. The query planner's nodes, the executor and the function library are internal |
| CLI and MCP assemblies | None. They are tools, not libraries | |
| Files | The `version` field and schema of the plugin registration file and the CSV mapping file; the JSON envelope version | |
| SQL | A statement returns or changes what its text says, or fails with a clear error | A result that was silently wrong |

## 3. What may change in a patch, a minor and a major

3.1 A patch fixes behaviour so that it matches the contract. It adds nothing and removes nothing.

3.2 A minor may add: commands, options, fields, tools, API members, deprecation notices.

3.3 Only a major removes or changes something in the contract.

Rules 3.1 to 3.3 are applied to every pull request: its description carries the required "Breaking" line, a pull request that is breaking carries the `breaking-change` label, and the release-plan check refuses a patch or minor version of a package whose release contains a pull request with that label.

3.4 A data-safety fix is the exception to 3.1 to 3.3. A change that stops PPDS writing wrong data, changing rows the statement did not select, writing to the wrong environment, or returning a silently wrong result may ship in any release, even when it refuses something that was accepted before. It always comes with an advisory (section 6).

3.5 Raising a floor (dropping a target framework, raising the minimum VS Code version or the minimum C library version) is a breaking change, with one exception: a target framework that its vendor no longer supports may be dropped in a minor release, if the release before it announced the drop. Adding a target framework is a minor change.

3.6 Exit codes and error codes are append-only within a major. Changing which code a command returns for an existing outcome is a breaking change, unless it moves a user error out of the internal-error code.

## 4. Deprecation and removal

4.1 Something in the contract is removed or changed only in a major. The release notes for that major list each removal and change in a "read before upgrading" section, with what to use instead.

4.2 Where a replacement exists before the major, the old form prints a deprecation notice that names the replacement (CLI: on stderr; MCP: a warning in the result; extension: a notification).

4.3 No document states a removal date or a waiting period unless the maintainer's decision for that specific removal is linked next to it.

## 5. Compatibility between packages

5.1 An extension release bundles one CLI from the same commit. A stable extension never bundles a pre-release CLI.

5.2 The extension checks the daemon's protocol version when it connects and refuses a daemon with a different one. It does not fall back to a `ppds` found on PATH unless a setting says so.

5.3 A library's dependency on another PPDS library names a published version.

## 6. Data-correctness advisories

6.1 When a released version returns wrong data or writes data the user did not ask for, the fix is announced in an advisory that lists the affected versions, the fixed version, what to check, and a workaround. The advisory is published in three places: a pinned issue, a notice in the README and the docs, and the release notes. When the defect fits the format of a GitHub security advisory, one is published as well.

6.2 The maintainer marks the affected NuGet versions deprecated. Versions are never unlisted.

6.3 The extension shows a one-time notice after it updates across such a fix.

## 7. Support

7.1 The current major of each package receives fixes. Earlier majors receive none; their users are directed to the advisory and the current major.

7.2 A release is made when its gates pass, not because time has passed. A data-safety or security fix that is merged and still unreleased past the targets in the security policy opens a check-in issue.

## 8. Public API baselines

8.1 Every published library records its public API in baseline files, and a build that changes the API without changing the baseline fails.

8.2 The baseline is rolled over on each stable tag of that package.

## 9. Decisions in documents

9.1 A sentence in a spec, changelog, release note or pull request that states an approval, an exception, a removal or a date links the issue comment where the maintainer made that decision. Without the link, it is marked "proposed".

9.2 The maintainer creates and pushes every tag and makes every publication. Neither automation nor an agent session tags, publishes, unpublishes or deprecates; a session prepares the commands and verifies the result.

## Not yet enforced

The rules above are the policy. The checks below are not built yet; until one is, its rule is applied in review.

| Rule | Not built yet | Issue |
|---|---|---|
| 1.2 | The release verifier does not check that PPDS.Query and PPDS.Migration take a major in the same release as PPDS.Dataverse | None yet |
| 1.5 | The CLI update check still treats an odd CLI minor as a pre-release line | [#1744](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1744) |
| 1.6 | The release-input verifier expects exactly one tag per package on the release commit, so it refuses a stable tag that shares a commit with its candidate tags | None yet |
| 3.1 to 3.3 | The Changelog Check reads the "Breaking" statement and the `breaking-change` label, and the release plan refuses a patch or minor plan for a package a labelled pull request changed ([RELEASE.md](RELEASE.md#breaking-changes-refuse-a-non-major-plan)); the Changelog Check is not yet a required status check, so a failure does not stop a merge | [#1789](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1789) |
| 3.6 | The values of the exit codes are pinned by a test; the table of the codes each command returns, and the test that checks it, are not built | [#1672](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1672) |
| 5.2 | The daemon has no protocol version, and the extension still falls back to `ppds` on PATH | [#1729](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1729) |
| 5.3 | The release-input verifier checks that each internal package dependency matches that package's tag on the same commit; it does not check that the version is published | None yet |
| 6.3 | The extension's one-time notice is not built | [#1665](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1665) |
| 7.2 | The scheduled check is still the 8-week release-cadence check, and the security policy states no targets for releasing a merged fix | [#1791](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1791) |
| Section 2 (Libraries), 8.1, 8.2 | Only PPDS.Auth has API baseline files and the analyzer; PPDS.Dataverse, PPDS.Query, PPDS.Migration and PPDS.Plugins have none | [#1219](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1219) |
| 9.1 | No check finds a decision sentence without a link | [#1790](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1790) |
