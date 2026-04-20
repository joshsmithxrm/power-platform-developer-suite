#!/usr/bin/env python3
"""Tests for scripts/retro_html_generator.py.

Covers:

- render_summary produces valid-looking HTML with all expected sections.
- render_summary handles empty / minimal inputs without crashing.
- Finding badges are emitted per row with correct data attributes (for JS filter).
- Markdown narrative is rendered (headings, lists, code, inline).
- HTML escaping is applied to user-supplied text.
- Mermaid flow is embedded.
- render_index enumerates retro files deterministically.
- CLI entry point writes a file and returns 0.
- CLI --index mode writes an index page.
"""
from __future__ import annotations

import json
import os
import sys
import tempfile

import pytest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

import retro_html_generator as gen  # noqa: E402


# ---------------- render_summary ------------------------------------------


class TestRenderSummary:
    def _sample_findings(self):
        return {
            "session_date": "2026-04-19",
            "pr": "#NNN",
            "stats": {
                "total_commits": 12,
                "feat_fix_ratio": "4/8",
                "thrashing_incidents": 1,
                "sessions_to_success": 2,
                "user_interrupts": 3,
                "severity": "rough",
            },
            "findings": [
                {
                    "id": "R-01",
                    "tier": "issue-only",
                    "kind": "code",
                    "description": "Pipeline resumes while previous stage still running.",
                    "fix_description": "Add a stage-in-flight guard.",
                    "confidence": "HIGH",
                    "verb": "DO_NOW",
                    "routed_to": "PR #834",
                    "status": "landed",
                    "files": ["scripts/pipeline.py"],
                },
                {
                    "id": "R-02",
                    "tier": "draft-fix",
                    "kind": "rule-drift",
                    "description": "SKILL.md says X but behavior does Y.",
                    "fix_description": "Update SKILL.md §3.",
                    "confidence": "LOW",
                    "verb": "DEFER",
                    "routed_to": "issue #999",
                    "status": "open",
                    "files": [".claude/skills/retro/SKILL.md"],
                },
                {
                    "id": "R-03",
                    "tier": "observation",
                    "kind": "code",
                    "description": "109-minute gap between stages.",
                    "fix_description": "",
                    "confidence": "HIGH",
                    "verb": "DROP",
                    "status": "dropped",
                },
            ],
        }

    def test_full_document_structure(self):
        html = gen.render_summary(self._sample_findings(), summary_md="## Narrative\n\nA story.")
        assert html.startswith("<!DOCTYPE html>")
        assert "<title>" in html
        assert "<style>" in html
        assert "</html>" in html
        assert "Findings" in html
        assert "Retro flow" in html
        assert "mermaid" in html.lower()

    def test_finding_rows_emitted_with_data_attrs(self):
        html = gen.render_summary(self._sample_findings())
        assert 'data-tier="issue-only"' in html
        assert 'data-verb="DO_NOW"' in html
        assert 'data-tier="draft-fix"' in html
        assert 'data-verb="DEFER"' in html
        # Rule-drift kind gets its own badge even when tier differs
        assert "kind-rule-drift" in html

    def test_stats_rendered(self):
        html = gen.render_summary(self._sample_findings())
        assert "12" in html  # commits
        assert "4/8" in html  # ratio
        assert "rough" in html

    def test_badges_for_verbs_and_tiers(self):
        html = gen.render_summary(self._sample_findings())
        assert "tier-issue-only" in html
        assert "tier-draft-fix" in html
        assert "tier-observation" in html
        assert "verb-DO_NOW" in html
        assert "verb-DEFER" in html
        assert "verb-DROP" in html

    def test_markdown_narrative_rendered(self):
        md = (
            "## Heading\n\n"
            "A paragraph with **bold** and `code` and a [link](https://example.com).\n\n"
            "- item one\n"
            "- item two\n"
        )
        html = gen.render_summary(self._sample_findings(), summary_md=md)
        assert "<h2>Heading</h2>" in html
        assert "<strong>bold</strong>" in html
        assert "<code>code</code>" in html
        assert '<a href="https://example.com">link</a>' in html
        assert "<li>item one</li>" in html

    def test_html_escapes_user_text(self):
        data = self._sample_findings()
        data["findings"][0]["description"] = "<script>alert('x')</script>"
        html = gen.render_summary(data)
        assert "<script>alert" not in html
        assert "&lt;script&gt;alert" in html

    def test_empty_findings_list(self):
        data = {"session_date": "2026-04-19", "findings": []}
        html = gen.render_summary(data)
        assert "No findings." in html

    def test_missing_optional_fields(self):
        data = {"findings": [{"id": "R-99", "tier": "observation"}]}
        # Must not raise
        html = gen.render_summary(data)
        assert "R-99" in html
        assert "tier-observation" in html

    def test_mermaid_diagram_embedded(self):
        html = gen.render_summary(self._sample_findings())
        assert "flowchart TD" in html
        assert "Mechanical Extraction" in html
        assert "Plan-with-defaults" in html or "plan-with-defaults" in html

    def test_filter_controls_present(self):
        html = gen.render_summary(self._sample_findings())
        assert 'id="filter"' in html
        assert 'id="filter-tier"' in html
        assert 'id="filter-verb"' in html

    def test_breadcrumb_links_to_index(self):
        html = gen.render_summary(self._sample_findings(), index_href="findings-index.html")
        assert 'href="findings-index.html"' in html


# ---------------- markdown_to_html ----------------------------------------


class TestMarkdownToHtml:
    def test_headings(self):
        assert "<h1>Foo</h1>" in gen.markdown_to_html("# Foo")
        assert "<h2>Foo</h2>" in gen.markdown_to_html("## Foo")
        assert "<h3>Foo</h3>" in gen.markdown_to_html("### Foo")

    def test_bullets(self):
        out = gen.markdown_to_html("- a\n- b\n- c")
        assert out.count("<li>") == 3
        assert "<ul>" in out and "</ul>" in out

    def test_paragraph(self):
        out = gen.markdown_to_html("A sentence across\ntwo lines.")
        assert "<p>" in out
        assert "A sentence across two lines." in out

    def test_inline_formatting(self):
        out = gen.markdown_to_html("**bold** *it* `code`")
        assert "<strong>bold</strong>" in out
        assert "<em>it</em>" in out
        assert "<code>code</code>" in out

    def test_fenced_code(self):
        out = gen.markdown_to_html("```\nline1\nline2\n```")
        assert "<pre><code>" in out
        assert "line1\nline2" in out

    def test_escapes_inside_code(self):
        out = gen.markdown_to_html("```\n<html>\n```")
        assert "&lt;html&gt;" in out

    def test_empty_input(self):
        assert gen.markdown_to_html("") == ""
        assert gen.markdown_to_html(None) == ""


# ---------------- render_index --------------------------------------------


class TestRenderIndex:
    def test_enumerates_retros(self):
        with tempfile.TemporaryDirectory() as tmp:
            for d in ("2026-04-19", "2026-03-15", "2026-02-01"):
                with open(os.path.join(tmp, d + "-summary.md"), "w") as f:
                    f.write("# retro\n")
                with open(os.path.join(tmp, d + "-summary.html"), "w") as f:
                    f.write("<html></html>")
            html = gen.render_index(tmp)
            # Newest first
            i1 = html.find("2026-04-19")
            i2 = html.find("2026-03-15")
            i3 = html.find("2026-02-01")
            assert 0 <= i1 < i2 < i3

    def test_empty_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            html = gen.render_index(tmp)
            assert "No retros yet." in html

    def test_nonexistent_dir(self):
        html = gen.render_index("/path/definitely/does/not/exist/xyz")
        assert "No retros yet." in html

    def test_ignores_unrelated_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "summary.json"), "w") as f:
                f.write("{}")
            with open(os.path.join(tmp, "README.md"), "w") as f:
                f.write("# hi")
            with open(os.path.join(tmp, "2026-04-19-summary.md"), "w") as f:
                f.write("# retro")
            html = gen.render_index(tmp)
            assert "2026-04-19" in html
            assert "summary.json" not in html
            assert "README" not in html


# ---------------- CLI -----------------------------------------------------


class TestCli:
    def _write_findings(self, tmp: str) -> str:
        path = os.path.join(tmp, "retro-findings.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(
                {
                    "session_date": "2026-04-19",
                    "pr": "#123",
                    "stats": {"severity": "clean", "total_commits": 3},
                    "findings": [
                        {
                            "id": "R-01",
                            "tier": "observation",
                            "kind": "code",
                            "description": "d",
                            "fix_description": "f",
                            "confidence": "HIGH",
                            "verb": "DROP",
                            "status": "dropped",
                        }
                    ],
                },
                f,
            )
        return path

    def test_cli_writes_summary_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            findings = self._write_findings(tmp)
            out = os.path.join(tmp, "sub", "out.html")
            rc = gen.main(["--findings", findings, "--out", out])
            assert rc == 0
            assert os.path.exists(out)
            with open(out, "r", encoding="utf-8") as f:
                body = f.read()
            assert "<!DOCTYPE html>" in body
            assert "R-01" in body

    def test_cli_summary_file_with_markdown(self):
        with tempfile.TemporaryDirectory() as tmp:
            findings = self._write_findings(tmp)
            md_path = os.path.join(tmp, "summary.md")
            with open(md_path, "w", encoding="utf-8") as f:
                f.write("## Narrative\n\nClean run.\n")
            out = os.path.join(tmp, "out.html")
            rc = gen.main(
                ["--findings", findings, "--summary", md_path, "--out", out]
            )
            assert rc == 0
            with open(out, "r", encoding="utf-8") as f:
                body = f.read()
            assert "<h2>Narrative</h2>" in body
            assert "Clean run." in body

    def test_cli_index_mode(self):
        with tempfile.TemporaryDirectory() as tmp:
            retros = os.path.join(tmp, ".retros")
            os.makedirs(retros)
            with open(os.path.join(retros, "2026-04-19-summary.md"), "w") as f:
                f.write("# retro\n")
            out = os.path.join(retros, "findings-index.html")
            rc = gen.main(["--index", "--retros-dir", retros, "--out", out])
            assert rc == 0
            with open(out, "r", encoding="utf-8") as f:
                body = f.read()
            assert "Retros" in body
            assert "2026-04-19" in body

    def test_cli_missing_findings_without_index_flag(self, capsys):
        rc = gen.main(["--out", "unused.html"])
        assert rc == 2
        captured = capsys.readouterr()
        assert "findings" in captured.err.lower()

    def test_cli_custom_title(self):
        with tempfile.TemporaryDirectory() as tmp:
            findings = self._write_findings(tmp)
            out = os.path.join(tmp, "out.html")
            rc = gen.main(
                ["--findings", findings, "--out", out, "--title", "Custom Retro Title"]
            )
            assert rc == 0
            with open(out, "r", encoding="utf-8") as f:
                body = f.read()
            assert "<title>Custom Retro Title</title>" in body


# ---------------- Integration-ish ----------------------------------------


class TestSmokeRoundtrip:
    def test_render_then_parse_doctype_and_wellformed_tags(self):
        """A weak invariant: output has matched tag counts for critical tags."""
        data = {
            "session_date": "2026-04-19",
            "findings": [
                {"id": "R-01", "tier": "observation", "description": "x"},
                {"id": "R-02", "tier": "auto-fix", "description": "y"},
            ],
        }
        html = gen.render_summary(data)
        assert html.count("<html") == 1
        assert html.count("</html>") == 1
        assert html.count("<body>") == 1
        assert html.count("</body>") == 1
        # One row per finding + header
        assert html.count("<tr") >= 3
