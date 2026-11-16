# Hosted compatibility artifact transport

This private package is the public-data transport for the disposable-key compatibility workflow. It is not a signing implementation or an independent builder. The Python coordinator owns stage admission, exact stage inventories, key custody and the COMPAT01–06 driver.

Install the locked dependency graph with `npm ci --ignore-scripts --no-audit --no-fund` in an external staging directory **before any key exists**. Do not install into the source checkout. The local JavaScript action wrapper itself uses only Node builtins; it starts the fixed coordinator with isolated Python (`-I -B`) and preserves the job runtime context for child artifact commands.

The direct dependency is exactly `@actions/artifact` 6.3.1. Its default current-run client uses the job's artifact runtime token. No `findBy`, GitHub token input, REST fallback or cross-run access is used. The workflow needs only its existing checkout permission. Never print, copy or persist the runtime token.

```text
node cli.mjs upload --name NAME --root ROOT --inventory FILES_JSON --binding BINDING_JSON --result RESULT_JSON [--timeout-seconds 300]
node cli.mjs download --name NAME --dest NEW_DIRECTORY --binding BINDING_JSON --result RESULT_JSON --timeout-seconds SECONDS [--expected-id ID --expected-digest SHA256]
```

`FILES_JSON` is an explicit JSON array of relative filenames. The root must contain exactly those regular files. The binding object has exactly these string fields: `repository`, `run_id`, `run_attempt`, `source_sha`, `experiment_id`, `stage`. Repository, run, attempt and SHA must match the action environment. The name is `ppds-compat-{run_id}-{run_attempt}-{source_sha}-{experiment_id}-{stage}`. VERIFY must pass the completed producer job's expected ID and digest; the coordinator also checks the returned inventory against its exact stage allowlist.

The success file contains `{schema_version:1, operation, artifact:{id,name,digest,size}, binding, inventory:[{path,size,sha256}]}`; download adds `destination`. It is written exclusively after all checks pass. No result file means failure or an uncertain outcome, never permission to retry an upload/sign operation. No remote artifact is deleted or overwritten.

The upload is one raw JSON envelope (`skipArchive:true`) containing the binding, per-file hashes and base64 public bytes. Download uses `skipDecompress:true`, checks both the client's digest result and an independent envelope SHA256, then validates every embedded file before writing a new destination. ZIP extraction never occurs. Limits are 16 files, 32 MiB decoded total and 48 MiB envelope. Traversal, Windows device names/streams, case collisions, links, private-key filenames, unlisted files and duplicate artifacts fail closed. A CLI process deadline bounds the client's internal waits and retries; the action also has a 65-minute coordinator limit.

These controls establish transport integrity under the accepted trusted-administrator/controlled-producer model. A producer can describe its own malicious content accurately; the artifact digest is not independent build provenance. Filename checks cannot classify arbitrary secret bytes hidden under an otherwise permitted name. The coordinator's explicit public inventory and key isolation remain necessary. This package never uploads a directory wildcard, a key path, or a signing secret.

Offline tests use an injected fake artifact client and a harmless temporary Python coordinator; they do not contact Actions, create a signing key or run signing. The first hosted use must still prove that the pinned client's raw-artifact service path works in the actual runner context.

Primary API references: [official package interfaces](https://github.com/actions/toolkit/blob/main/packages/artifact/src/internal/shared/interfaces.ts), [current-run client selection](https://github.com/actions/toolkit/blob/main/packages/artifact/src/internal/client.ts), [artifact authentication](https://github.com/actions/toolkit/blob/main/packages/artifact/src/internal/shared/artifact-twirp-client.ts).

The 2026-11-16 dependency audit reported two moderate advisory roots across six affected dependency nodes, with no high/critical findings: [glob cancellation](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4) and [minimatch pattern amplification](https://github.com/advisories/GHSA-g3hv-75wg-m249). These arrive through the package's archiver dependencies. This adapter's raw-file upload branch bypasses archive creation and its raw download branch bypasses extraction; it never accepts glob patterns. That is a bounded source-path assessment, not a claim that the installed dependency graph is vulnerability-free. Recheck this assessment if archive/glob support or the dependency lock changes. The audit's suggested downgrade to artifact2.1.4 would remove the required raw-file APIs and was not applied.
