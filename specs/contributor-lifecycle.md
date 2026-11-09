# Contributor Lifecycle

**Status:** Implemented (offline checks; hosted enforcement not adopted)
**Last Updated:** 2026-11-03
**Surfaces:** Contributor and maintainer workflows
**Code:** `scripts/ci/check_contributor_provenance.py`, `tests/ci/test_contributor_provenance.py`, `docs/CONTRIBUTOR-LIFECYCLE.md`

## Overview

Preserve contributor provenance, actual squash credit and named closeout ownership through landing and release.

## Contract

The shared, tool-neutral maintainer policy lives in
[`docs/CONTRIBUTOR-LIFECYCLE.md`](../docs/CONTRIBUTOR-LIFECYCLE.md).
Prefer landing the original contributor PR directly when practical. Incorporation
elsewhere needs explicit source, reason, credit and named closeout ownership.
Branch ancestry alone never proves the resulting squash commit credits its author.

The offline validator reads an explicit provenance record and optional captured
proposal, landed-commit and release snapshots. It performs no network access,
semantic overlap detection, merge, communication or repository mutation. A valid
declaration is not independently verified external evidence. It cannot infer the
actual squash author from PR ownership or commit ancestry. Unknown evidence stays
unknown. A no-contribution declaration needs no invented identity or approval.

The JSON contract and stage-specific commands are documented with the shared
policy. Stages are plan, pre-merge, post-landing and post-release. Contribution
modes are none, direct and incorporated. Applicable records bind source PR/head,
verified contributor identity, landing PR/head, intended disposition, and named
communication/disposition owners. Supplied merge proposal author/message and
landed commit credit must credit every verified contributor as author or as a
proper final Co-authored-by trailer. Release checks require literal source and
identity acknowledgement in the supplied notes. Scope and limitations are visible
in machine-readable output; unresolved closeout never becomes completed work.

No workflow or branch-protection activation is part of this change. Existing
implementation, PR and release procedures reference this one policy and require
its applicable checks at their decision points. Future hosted enforcement needs
its own adoption review, including trusted input/code and existing-PR treatment.

## Acceptance Criteria

| ID | Criterion | Test | Status |
| --- | --- | --- | --- |
| AC-01 | Explicit no-contribution records pass without fabricating contributors or requiring merge approval; contradictory source declarations fail. | `test_none_requires_no_identity_snapshot_or_approval`, `test_none_rejects_contradictory_provenance` | ✅ |
| AC-02 | Direct landing binds the original source PR/head to the landing PR/head; incorporation requires a reason and exact source provenance. | `test_direct_binds_original_pr_and_head`, `test_incorporation_requires_reason_distinct_sources_and_unique_records` | ✅ |
| AC-03 | Applicable records require named communication/disposition owners and an explicit intended source-PR disposition. | `test_named_owners_cannot_be_blank_or_placeholders`, `test_explicit_disposition_and_exact_reply_are_required` | ✅ |
| AC-04 | Pre-merge checks evaluate the supplied actual proposal author/message and reject missing contributor credit even when source commits remain in branch history. | `test_actual_proposal_credit_not_body_or_branch_ancestry`, `test_real_cli_valid_plan_and_actual_proposal` | ✅ |
| AC-05 | Only proper final co-author trailers or actual authorship satisfy credit; arbitrary body mentions and malformed trailers do not. | `test_supported_final_trailers_credit_every_contributor`, `test_malformed_nonfinal_wrapped_or_missing_separator_trailers_do_not_credit` | ✅ |
| AC-06 | Unverified identities, unknown proposal authors and stale landing PR/head snapshots cannot satisfy pre-merge verification. | `test_unknown_contributor_is_plan_only`, `test_unknown_author_stale_pr_or_head_cannot_verify` | ✅ |
| AC-07 | Post-landing checks verify actual landed commit credit and retain unknown outcomes rather than inheriting pre-merge success. | `test_post_landing_checks_actual_credit_independently` | ✅ |
| AC-08 | Pending permission requires named ownership, exact prepared reply and explicit disposition; it is never reported as completed communication or closure. | `test_pending_approval_stays_visible_and_blocks_release_closeout`, `test_completed_closeout_requires_evidence_not_state_labels`, `test_keep_open_is_explicit_owned_closeout_not_an_automatic_close` | ✅ |
| AC-09 | Post-release checks require the actual included landing commit and contributor/source acknowledgement in supplied release notes. | `test_release_requires_matching_actual_commit_and_literal_credit` | ✅ |
| AC-10 | Invalid JSON, duplicate keys, unsupported versions/stages and malformed identities fail safely; the command reads local files only and reports bounded validation scope. | `test_malformed_versions_types_identities_urls_and_heads_fail_safely`, `test_real_cli_invalid_json_duplicate_keys_and_constants_are_safe`, `test_real_cli_malformed_invocation_and_nonlocal_paths`, `test_large_and_deep_inputs_are_bounded`, `test_main_uses_local_reads_without_network_or_process_launch` | ✅ |
| AC-11 | The PR checklist and implementation, PR and release guidance resolve to the shared policy without requiring an AI client or authorizing external actions. | `test_entry_points_resolve_to_the_single_shared_policy`, `test_pr_template_exposes_routes_and_shared_policy_reference`, `test_documented_no_contribution_example_runs_without_other_evidence` | ✅ |
