# Contributor lifecycle

Maintainers own a contribution through landing, credit, communication and source-PR
disposition. This policy applies with any development tool, including no AI tool.
It grants no authority to merge, publish, send a reply or close someone else's PR.

## Choose the landing route early

Prefer merging the contributor's original PR directly when practical. Review and
update that PR rather than automatically moving its work to a maintainer branch.
If incorporating work elsewhere, record why the original route was not chosen,
the source PR and exact source head, verified contributor identity, landing PR/head
and intended source-PR disposition. Review size applies to the actual landing PR;
an oversized cumulative candidate does not establish that a smaller original PR
cannot land directly. Do not infer impossibility without evidence.

Use one of three explicit provenance modes:

- `none`: no external contribution is incorporated. State the reason; no source
  identity, attribution evidence or extra approval is required. Discussing an issue
  or citing an incident as evidence does not itself incorporate its author's code.
- `direct`: the original source PR is the landing PR. Capture its current exact
  head and contributor identities; additional source work needs its own record.
- `incorporated`: another PR lands the work. Record each source independently,
  including the reason, source head and contributor identity. Do not fabricate
  contributors from titles, usernames, branch names or semantic similarity.

If identity or evidence is unknown, record it as unverified. Resolve it before a
dependent credit/merge claim; do not substitute the maintainer or guess an email.
Use verified public commit identity or contributor-confirmed details. Do not expose
private contact information in a public record merely to satisfy a template.

## Check the actual merge proposal

For a squash merge, inspect the exact author identity and complete final message
that the selected tool/UI/API operation will submit or produce. Capture the
landing PR/head and evidence source. Preserved commits on a feature branch do not
prove the resulting squash commit will credit their authors. Explicit API title
or message parameters must be reviewed as the actual proposal, not replaced by an
assumed platform default.

Credit every verified contributor as the actual author or through a correctly
formed final `Co-authored-by: Name <email>` trailer. A body mention is insufficient.
Use only verified identity; retain all contributors when there are several. Check
the exact proposal again after editing its message or changing the head. If the
tool cannot establish the proposed author, keep it unknown and resolve that
specific limitation before merging. A check of an offline snapshot alone cannot
establish what a remote platform will do.

After landing, inspect the actual resulting commit author, full message and
trailers. Record its exact SHA and source evidence. A pre-merge pass is not a
post-landing result. If credit differs, report the discrepancy and prepare the
specific correction for the maintainer's decision; this policy does not authorize
rewriting history or changing historical release credit.

## Name the closeout owner

Before landing, assign named owners for both contributor communication and the
original PR's disposition. They may be the same person. Record the intended
decision (`merge-original`, `close-as-incorporated` or `keep-open`) and reason.
Use `merge-original` for direct landing; incorporation uses the other two choices.
For incorporation, explain the landing location and remaining source-PR work in
the prepared reply; keeping a PR open also needs an explicit reason and owner.

When sending or closing needs Josh's permission, prepare the exact reply and
closure/keep-open decision, mark them `pending-approval`, and bring that concrete
decision to him. Continue independent authorized work. Do not silently leave
closeout unowned, treat a draft as sent, or treat permission as implied by elapsed
time. Record evidence for actual communication and disposition when completed.

At release preparation, include the source PR and verified contributor credit in
the intended release acknowledgement. After publication, inspect the actual
published notes and included landing commit; verify the acknowledgement and report
any difference. A prepared note is not a published acknowledgement. Pending
communication/disposition remains visible until completed or explicitly decided.

## Lightweight checklist and local validation

The PR template prompts for the applicable mode and provenance record. Maintainers
can record evidence in the PR body, an attached review artifact or a local JSON
file; there is no requirement to commit personal information or a ledger for every
PR. No-contribution work uses this minimal record:

```json
{"schema_version": 1, "mode": "none", "reason": "No external contribution is incorporated."}
```

For applicable work, keep one record with these fields:

| Field | Required content |
| --- | --- |
| `schema_version`, `mode`, `reason` | Version 1; direct/incorporated; factual route reason |
| `landing_pr`, `landing_head` | Exact GitHub PR URL and full 40-character source head |
| `sources[].pr`, `sources[].head` | Each original PR and exact source head |
| `sources[].contributors[]` | `name`, `email`, optional `login`, `verified` boolean and `evidence`; unverified identity remains explicitly unknown |
| `sources[].disposition` | `decision`, `reason`, named `owner`, `state` (planned/pending-approval/done), evidence when done |
| `sources[].communication` | Named `owner`, `state` (prepared/pending-approval/sent), exact `reply`, evidence when sent |

Capture the actual proposal separately: version 1, `landing_pr`, `head`, `method`
(`squash`), verified `author` (`name`, `email`, `verified`, `evidence`), complete
`message` and capture `evidence`. A landed snapshot has version 1, `landing_pr`,
source `head`, actual `commit`, verified `author`, full `message` and `evidence`.
A release snapshot has version 1, `landing_pr`, that included landing `commit`,
actual published `notes` and capture `evidence`.

Run the relevant stage using any ordinary Python environment:

```text
python scripts/ci/check_contributor_provenance.py --record record.json --stage plan
python scripts/ci/check_contributor_provenance.py --record record.json --stage pre-merge --proposal proposal.json
python scripts/ci/check_contributor_provenance.py --record record.json --stage post-landing --landing landed.json
python scripts/ci/check_contributor_provenance.py --record record.json --stage post-release --landing landed.json --release release.json
```

The final co-author block uses single-line trailers after a blank line; wrapped or
malformed identity trailers must be corrected rather than assumed valid. The
validator checks exact supplied identities, PR/head bindings and literal release
acknowledgement. It cannot determine semantic overlap, independently authenticate
snapshots, infer remote merge authors, or judge whether prose fairly credits work.
Its proposal schema supports squash only; another authorized merge method needs
separate reviewed validation rather than a fabricated squash snapshot. This does
not establish a new repository-wide merge-strategy rule.
An omitted declaration or dishonest snapshot cannot be detected reliably by this
offline check. Human review remains responsible for these facts.

Exit 0 means the requested supplied record/stage passed its bounded checks; exit 1
means invalid or unfinished required work; exit 2 means invalid input/invocation.
Read the scope and pending-closeout fields, not just an exit code. A plan can retain
unverified information; later stages must not promote it to verified credit.
For applicable modes, each stage checks its required snapshots only; an extra
snapshot from another stage does not acquire a verification result. The command
still requires every supplied file to be bounded, well-formed local JSON.

## Adoption boundary

The policy and local validator are reviewed together. Existing implementation, PR
and release procedures reference this one policy. This change does not activate a
GitHub workflow, add branch protection, run a merge wrapper, or send any message.
It therefore does not claim automatic enforcement on all existing PRs. Hosted
adoption needs a separate reviewed change addressing trusted validator code,
untrusted PR input, actual merge-tool evidence, existing PRs and a safe
no-contribution path. Never turn a format check into an asserted semantic audit.
