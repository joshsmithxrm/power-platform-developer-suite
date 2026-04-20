#!/usr/bin/env python3
"""Retro HTML synthesis generator.

Produces a self-contained HTML dashboard from a retro-findings.json (and
optionally a retro-summary.md). The output is static HTML with embedded
Mermaid (via CDN) and vanilla JS filtering — no build step, no framework.

Three modes:

1. Single-retro synthesis::

     python scripts/retro_html_generator.py \\
       --findings .workflow/retro-findings.json \\
       --summary  .retros/2026-04-19-summary.md \\
       --out      .retros/2026-04-19-summary.html

2. Cross-retro navigation index::

     python scripts/retro_html_generator.py \\
       --index \\
       --retros-dir .retros \\
       --out .retros/findings-index.html

3. Programmatic use — import :func:`render_summary` or :func:`render_index`.

See :mod:`tests.test_retro_html_generator` for behavioural tests.
"""
from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
from typing import Any, Iterable


# ----- Mermaid diagram ------------------------------------------------------

RETRO_FLOW_MERMAID = """\
flowchart TD
    A[1. Invoke /retro scope] --> B[2. Scope + Carryover]
    B --> C[3. Mechanical Extraction<br/><i>subprocess</i>]
    C --> D[4. Main-Session Analysis<br/><i>main Claude only</i>]
    D --> E[5. Decision Phase<br/><i>plan-with-defaults</i>]
    E -->|DO NOW| F[6. Routing]
    E -->|DEFER| F
    E -->|DROP| F
    E -->|RESEARCH-FIRST| R[/investigate] --> E
    F -->|/pr| G[7. Monitor &amp; Confirm]
    F -->|/backlog| G
    G --> H[8. Executive Synthesis<br/>md + HTML]
    H --> I[9. Persist<br/>.retros/summary.json + index.html]
"""


# ----- Template pieces ------------------------------------------------------

_HEAD_CSS = """\
:root {
  --bg: #0e1116;
  --panel: #171b22;
  --panel-2: #20252e;
  --text: #e6edf3;
  --muted: #8b949e;
  --accent: #58a6ff;
  --ok: #3fb950;
  --warn: #d29922;
  --err: #f85149;
  --border: #30363d;
}
* { box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  background: var(--bg);
  color: var(--text);
  margin: 0;
  padding: 2rem;
  line-height: 1.5;
}
h1, h2, h3 { font-weight: 600; letter-spacing: -0.01em; }
h1 { font-size: 1.75rem; margin: 0 0 0.25rem 0; }
h2 { font-size: 1.25rem; margin: 2rem 0 0.75rem 0; border-bottom: 1px solid var(--border); padding-bottom: 0.25rem; }
.meta { color: var(--muted); font-size: 0.9rem; margin-bottom: 1.5rem; }
.meta a { color: var(--accent); text-decoration: none; }
.meta a:hover { text-decoration: underline; }
.panel { background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 1rem; margin-bottom: 1rem; }
.controls { display: flex; gap: 0.5rem; flex-wrap: wrap; align-items: center; margin-bottom: 0.75rem; }
.controls input, .controls select {
  background: var(--panel-2);
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 0.35rem 0.5rem;
  font-size: 0.9rem;
}
.controls input { flex: 1; min-width: 12rem; }
table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
th, td { text-align: left; padding: 0.5rem 0.6rem; border-bottom: 1px solid var(--border); vertical-align: top; }
th { color: var(--muted); font-weight: 500; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em; }
tr.hidden { display: none; }
.badge {
  display: inline-block;
  padding: 0.1rem 0.5rem;
  border-radius: 999px;
  font-size: 0.75rem;
  font-weight: 500;
  background: var(--panel-2);
  border: 1px solid var(--border);
  color: var(--muted);
  white-space: nowrap;
}
.badge.tier-auto-fix { background: #1f6feb33; color: #79c0ff; border-color: #1f6feb66; }
.badge.tier-draft-fix { background: #d2992233; color: #e3b341; border-color: #d2992266; }
.badge.tier-issue-only { background: #f8514933; color: #ff7b72; border-color: #f8514966; }
.badge.tier-observation { background: #8b949e33; color: #c9d1d9; }
.badge.verb-DO_NOW, .badge.verb-DO-NOW { background: #3fb95033; color: #56d364; border-color: #3fb95066; }
.badge.verb-DEFER { background: #d2992233; color: #e3b341; border-color: #d2992266; }
.badge.verb-DROP { background: #8b949e33; color: #c9d1d9; }
.badge.verb-RESEARCH_FIRST, .badge.verb-RESEARCH-FIRST { background: #58a6ff33; color: #79c0ff; border-color: #58a6ff66; }
.badge.kind-rule-drift { background: #bc8cff33; color: #d2a8ff; border-color: #bc8cff66; }
.badge.status-landed { background: #3fb95033; color: #56d364; }
.badge.status-open { background: #d2992233; color: #e3b341; }
.badge.status-blocked { background: #f8514933; color: #ff7b72; }
.badge.status-dropped { background: #8b949e33; color: #c9d1d9; }
.confidence-HIGH { color: var(--ok); font-weight: 500; }
.confidence-LOW { color: var(--warn); font-weight: 500; }
.narrative { white-space: pre-wrap; color: var(--text); }
.narrative h1, .narrative h2, .narrative h3 { color: var(--text); }
.mermaid-wrap { background: #fff; border-radius: 6px; padding: 1rem; }
.nav-breadcrumb { font-size: 0.85rem; color: var(--muted); margin-bottom: 1rem; }
.nav-breadcrumb a { color: var(--accent); text-decoration: none; }
.empty { color: var(--muted); font-style: italic; }
.stats { display: flex; gap: 1.5rem; flex-wrap: wrap; }
.stat { background: var(--panel-2); border: 1px solid var(--border); border-radius: 4px; padding: 0.5rem 0.75rem; }
.stat .k { color: var(--muted); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em; }
.stat .v { font-size: 1.1rem; font-weight: 500; }
"""

_FILTER_JS = """\
(function() {
  var input = document.getElementById('filter');
  var tierSel = document.getElementById('filter-tier');
  var verbSel = document.getElementById('filter-verb');
  var rows = Array.prototype.slice.call(document.querySelectorAll('#findings-table tbody tr'));
  function apply() {
    var q = (input && input.value || '').toLowerCase();
    var tier = tierSel && tierSel.value || '';
    var verb = verbSel && verbSel.value || '';
    rows.forEach(function(row) {
      var text = row.textContent.toLowerCase();
      var rowTier = row.getAttribute('data-tier') || '';
      var rowVerb = row.getAttribute('data-verb') || '';
      var matchQ = !q || text.indexOf(q) !== -1;
      var matchTier = !tier || rowTier === tier;
      var matchVerb = !verb || rowVerb === verb;
      if (matchQ && matchTier && matchVerb) { row.classList.remove('hidden'); }
      else { row.classList.add('hidden'); }
    });
  }
  [input, tierSel, verbSel].forEach(function(el) {
    if (el) el.addEventListener('input', apply);
    if (el) el.addEventListener('change', apply);
  });
})();
"""


# ----- Markdown → HTML (minimal, deterministic) -----------------------------

# CommonMark-adaptive inline code: a run of N backticks opens a span that
# closes at the next run of exactly N backticks. Allows embedded runs of
# fewer than N backticks (e.g. ``a ` b`` renders <code>a ` b</code>).
_INLINE_CODE = re.compile(r"(`+)(.+?)(?<!`)\1(?!`)", re.DOTALL)
_BOLD = re.compile(r"\*\*([^*]+)\*\*")
_ITALIC = re.compile(r"(?<!\*)\*([^*]+)\*(?!\*)")
_LINK = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")


def _code_span_inner(content: str) -> str:
    """CommonMark code-span normalisation: strip a single leading/trailing
    space iff the content both starts and ends with a space and is not
    entirely whitespace. Operates on already-html-escaped text."""
    if (
        len(content) >= 2
        and content.startswith(" ")
        and content.endswith(" ")
        and content.strip() != ""
    ):
        return content[1:-1]
    return content


def _sanitize_url(url: str) -> str:
    """Return *url* if it uses a safe scheme, else ``"#"``. Input is assumed
    to have been html-escaped already (so ``&`` appears as ``&amp;``). Control
    characters and whitespace are stripped before scheme inspection to defeat
    obfuscated ``java\\tscript:`` variants."""
    if not url:
        return "#"
    # Strip ASCII control chars + whitespace that browsers otherwise ignore
    # when parsing the scheme (``java\tscript:foo`` is treated as javascript).
    cleaned = "".join(ch for ch in url if ord(ch) > 0x20).lstrip()
    lower = cleaned.lower()
    # Relative / fragment / path URLs — allow.
    if lower.startswith(("#", "/", "./", "../")):
        return url
    # Scheme-relative ``//host/...`` — allow (browser inherits page scheme).
    if lower.startswith("//"):
        return url
    # Has an explicit scheme? Only allow the whitelist.
    # Use the cleaned form for the scheme test so ``java\tscript:`` is caught.
    colon = lower.find(":")
    if colon == -1:
        # No scheme and not anchored — treat as relative.
        return url
    scheme = lower[: colon + 1]
    if scheme in ("http:", "https:", "mailto:"):
        return url
    return "#"


def _inline_md(text: str) -> str:
    """Render inline Markdown to HTML. Assumes *text* is already html-escaped."""
    text = _INLINE_CODE.sub(
        lambda m: "<code>" + _code_span_inner(m.group(2)) + "</code>", text
    )
    text = _BOLD.sub(lambda m: "<strong>" + m.group(1) + "</strong>", text)
    text = _ITALIC.sub(lambda m: "<em>" + m.group(1) + "</em>", text)
    text = _LINK.sub(
        lambda m: '<a href="' + _sanitize_url(m.group(2)) + '">' + m.group(1) + "</a>",
        text,
    )
    return text


def markdown_to_html(md: str) -> str:
    """Very small Markdown-to-HTML renderer. Handles what retro summaries use:
    headings, lists, paragraphs, inline code, bold, italic, links, fenced code.

    Deliberately minimal — keeps the generator zero-dep. Not a spec-conformant
    CommonMark renderer and not intended as one.
    """
    if not md:
        return ""
    lines = md.splitlines()
    out: list[str] = []
    i = 0
    in_list = False
    in_code = False
    code_buf: list[str] = []
    para_buf: list[str] = []

    def flush_para():
        if para_buf:
            joined = " ".join(s.strip() for s in para_buf if s.strip())
            if joined:
                out.append("<p>" + _inline_md(html.escape(joined)) + "</p>")
            para_buf.clear()

    def close_list():
        nonlocal in_list
        if in_list:
            out.append("</ul>")
            in_list = False

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        if stripped.startswith("```"):
            if in_code:
                out.append("<pre><code>" + html.escape("\n".join(code_buf)) + "</code></pre>")
                code_buf = []
                in_code = False
            else:
                flush_para()
                close_list()
                in_code = True
            i += 1
            continue

        if in_code:
            code_buf.append(line)
            i += 1
            continue

        if not stripped:
            flush_para()
            close_list()
            i += 1
            continue

        m = re.match(r"^(#{1,6})\s+(.*)$", stripped)
        if m:
            flush_para()
            close_list()
            level = len(m.group(1))
            out.append(
                "<h{0}>{1}</h{0}>".format(level, _inline_md(html.escape(m.group(2))))
            )
            i += 1
            continue

        m = re.match(r"^[-*]\s+(.*)$", stripped)
        if m:
            flush_para()
            if not in_list:
                out.append("<ul>")
                in_list = True
            out.append("<li>" + _inline_md(html.escape(m.group(1))) + "</li>")
            i += 1
            continue

        # paragraph line
        close_list()
        para_buf.append(stripped)
        i += 1

    flush_para()
    close_list()
    if in_code and code_buf:
        out.append("<pre><code>" + html.escape("\n".join(code_buf)) + "</code></pre>")
    return "\n".join(out)


# ----- Finding rendering ----------------------------------------------------


def _badge(cls: str, text: str) -> str:
    return '<span class="badge {0}">{1}</span>'.format(
        html.escape(cls), html.escape(text)
    )


def _safe_get(obj: dict, key: str, default: str = "") -> str:
    v = obj.get(key, default)
    if v is None:
        return default
    return str(v)


def _render_finding_row(f: dict) -> str:
    fid = _safe_get(f, "id", "?")
    tier = _safe_get(f, "tier", "observation")
    kind = _safe_get(f, "kind", "code")
    desc = _safe_get(f, "description", "")
    fix = _safe_get(f, "fix_description", "")
    conf = _safe_get(f, "confidence", "")
    verb = _safe_get(f, "verb", "")
    routed = _safe_get(f, "routed_to", "")
    status = _safe_get(f, "status", "")
    files = f.get("files") or []

    conf_html = (
        '<span class="confidence-{0}">{1}</span>'.format(html.escape(conf), html.escape(conf))
        if conf
        else ""
    )

    badges = [_badge("tier-" + tier, tier)]
    if kind and kind != "code":
        badges.append(_badge("kind-" + kind, kind))
    if verb:
        badges.append(_badge("verb-" + verb, verb.replace("_", " ")))
    if status:
        badges.append(_badge("status-" + status, status))

    files_html = ""
    if files:
        files_html = "<br/><small>" + ", ".join(
            "<code>" + html.escape(str(p)) + "</code>" for p in files
        ) + "</small>"

    routed_html = ""
    if routed:
        routed_html = "<br/><small>→ " + _inline_md(html.escape(routed)) + "</small>"

    return (
        '<tr data-tier="{tier}" data-verb="{verb}">'
        '<td><strong>{fid}</strong></td>'
        '<td>{badges}</td>'
        '<td>{desc}{files}</td>'
        '<td>{fix}{routed}</td>'
        '<td>{conf}</td>'
        '</tr>'
    ).format(
        tier=html.escape(tier),
        verb=html.escape(verb),
        fid=html.escape(fid),
        badges=" ".join(badges),
        desc=_inline_md(html.escape(desc)),
        files=files_html,
        fix=_inline_md(html.escape(fix)),
        routed=routed_html,
        conf=conf_html,
    )


def _render_stats(stats: dict) -> str:
    if not stats:
        return ""
    items = []
    order = [
        ("total_commits", "Commits"),
        ("feat_fix_ratio", "Feat/Fix"),
        ("thrashing_incidents", "Thrashing"),
        ("sessions_to_success", "Sessions→OK"),
        ("user_interrupts", "Interrupts"),
        ("severity", "Severity"),
    ]
    for key, label in order:
        if key in stats:
            items.append(
                '<div class="stat"><div class="k">{k}</div><div class="v">{v}</div></div>'.format(
                    k=html.escape(label), v=html.escape(str(stats[key]))
                )
            )
    if not items:
        return ""
    return '<div class="stats">' + "".join(items) + "</div>"


# ----- Public rendering entry points ---------------------------------------


def render_summary(
    findings_data: dict,
    summary_md: str | None = None,
    *,
    title: str | None = None,
    index_href: str = "findings-index.html",
) -> str:
    """Render a single-retro synthesis page as a full HTML document."""
    session_date = findings_data.get("session_date", "")
    pr = findings_data.get("pr", "")
    stats = findings_data.get("stats") or {}
    findings = findings_data.get("findings") or []
    page_title = title or (
        "Retro " + (session_date or "") + (" — " + pr if pr else "")
    ).strip() or "Retro"

    rows_html = "\n".join(_render_finding_row(f) for f in findings) or (
        '<tr><td colspan="5" class="empty">No findings.</td></tr>'
    )

    narrative_html = markdown_to_html(summary_md) if summary_md else ""

    return _BASE_DOC.format(
        title=html.escape(page_title),
        css=_HEAD_CSS,
        breadcrumb='<div class="nav-breadcrumb"><a href="{0}">&larr; All retros</a></div>'.format(
            html.escape(index_href)
        ),
        header='<h1>{0}</h1><div class="meta">{1}{2}</div>'.format(
            html.escape(page_title),
            "Session date: " + html.escape(session_date) if session_date else "",
            (" · PR " + html.escape(pr)) if pr else "",
        ),
        stats_section=_render_stats(stats),
        narrative_section=(
            '<h2>Narrative</h2><div class="panel narrative">' + narrative_html + "</div>"
            if narrative_html
            else ""
        ),
        findings_section=(
            '<h2>Findings</h2>'
            '<div class="panel">'
            '<div class="controls">'
            '<input id="filter" placeholder="Filter by text..." aria-label="Filter findings" />'
            '<select id="filter-tier" aria-label="Filter by tier">'
            '<option value="">All tiers</option>'
            '<option value="auto-fix">auto-fix</option>'
            '<option value="draft-fix">draft-fix</option>'
            '<option value="issue-only">issue-only</option>'
            '<option value="observation">observation</option>'
            '</select>'
            '<select id="filter-verb" aria-label="Filter by verb">'
            '<option value="">All verbs</option>'
            '<option value="DO_NOW">DO NOW</option>'
            '<option value="DEFER">DEFER</option>'
            '<option value="DROP">DROP</option>'
            '<option value="RESEARCH_FIRST">RESEARCH-FIRST</option>'
            '</select>'
            '</div>'
            '<table id="findings-table">'
            '<thead><tr><th>ID</th><th>Classification</th><th>Issue</th><th>Fix / Routed</th><th>Conf.</th></tr></thead>'
            '<tbody>' + rows_html + '</tbody>'
            '</table>'
            '</div>'
        ),
        flow_section=(
            '<h2>Retro flow</h2>'
            '<div class="panel mermaid-wrap">'
            '<pre class="mermaid">' + html.escape(RETRO_FLOW_MERMAID) + '</pre>'
            '</div>'
        ),
        script='<script>' + _FILTER_JS + '</script>'
               '<script type="module">'
               'import mermaid from "https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.esm.min.mjs";'
               'mermaid.initialize({startOnLoad:true, theme:"default"});'
               '</script>',
    )


def render_index(retros_dir: str) -> str:
    """Render the cross-retro navigation index from the files in *retros_dir*."""
    entries = _discover_retro_entries(retros_dir)
    rows = []
    for entry in entries:
        md_link = ""
        html_link = ""
        if entry.get("md"):
            md_link = '<a href="{0}">md</a>'.format(html.escape(entry["md"]))
        if entry.get("html"):
            html_link = '<a href="{0}">html</a>'.format(html.escape(entry["html"]))
        links = " · ".join(x for x in (html_link, md_link) if x) or "<span class=\"empty\">no artifacts</span>"
        severity = entry.get("severity") or ""
        sev_badge = (
            '<span class="badge status-{0}">{0}</span>'.format(html.escape(severity))
            if severity
            else ""
        )
        rows.append(
            '<tr>'
            '<td><strong>{date}</strong></td>'
            '<td>{finding_count}</td>'
            '<td>{severity}</td>'
            '<td>{links}</td>'
            '</tr>'.format(
                date=html.escape(entry.get("date", "")),
                finding_count=html.escape(str(entry.get("finding_count", 0))),
                severity=sev_badge,
                links=links,
            )
        )
    body_rows = "\n".join(rows) or '<tr><td colspan="4" class="empty">No retros yet.</td></tr>'

    return _BASE_DOC.format(
        title="Retros — index",
        css=_HEAD_CSS,
        breadcrumb="",
        header='<h1>Retros</h1><div class="meta">Cross-retro navigation index</div>',
        stats_section="",
        narrative_section="",
        findings_section=(
            '<h2>Retros ({count})</h2>'
            '<div class="panel">'
            '<table>'
            '<thead><tr><th>Date</th><th>Findings</th><th>Severity</th><th>Artifacts</th></tr></thead>'
            '<tbody>' + body_rows + '</tbody>'
            '</table>'
            '</div>'
        ).format(count=len(entries)),
        flow_section="",
        script="",
    )


# ----- Index discovery ------------------------------------------------------

_SUMMARY_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})-summary\.(md|html)$")


def _discover_retro_entries(retros_dir: str) -> list[dict]:
    """Find every ``YYYY-MM-DD-summary.{md,html}`` pair under *retros_dir*.

    Also consults the persistent store (``summary.json``) for per-date
    metadata when a same-day ``retro-findings.json`` shard is not available.
    Deterministically ordered newest-first.
    """
    by_date: dict[str, dict] = {}
    if not os.path.isdir(retros_dir):
        return []

    for name in os.listdir(retros_dir):
        m = _SUMMARY_RE.match(name)
        if not m:
            continue
        date, ext = m.group(1), m.group(2)
        entry = by_date.setdefault(date, {"date": date})
        entry[ext] = name

    # Enrich with findings.json from .workflow/ if present
    workflow_findings = os.path.join(
        os.path.dirname(os.path.abspath(retros_dir)), ".workflow", "retro-findings.json"
    )
    if os.path.exists(workflow_findings):
        try:
            with open(workflow_findings, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            date = data.get("session_date")
            if date in by_date:
                by_date[date]["finding_count"] = len(data.get("findings") or [])
                by_date[date]["severity"] = (data.get("stats") or {}).get("severity", "")
        except (OSError, json.JSONDecodeError):
            pass

    for entry in by_date.values():
        entry.setdefault("finding_count", 0)
        entry.setdefault("severity", "")

    return sorted(by_date.values(), key=lambda e: e["date"], reverse=True)


# ----- Document skeleton ---------------------------------------------------

_BASE_DOC = """\
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>{title}</title>
<style>
{css}
</style>
</head>
<body>
{breadcrumb}
{header}
{stats_section}
{narrative_section}
{findings_section}
{flow_section}
{script}
</body>
</html>
"""


# ----- CLI ------------------------------------------------------------------


def _read_json(path: str) -> Any:
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def _read_text(path: str) -> str:
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def _parse_args(argv: Iterable[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="retro_html_generator",
        description="Generate retro synthesis HTML (dashboard or index).",
    )
    parser.add_argument("--findings", help="Path to retro-findings.json")
    parser.add_argument("--summary", help="Path to retro-summary.md (optional)")
    parser.add_argument("--out", required=True, help="Output HTML path")
    parser.add_argument("--title", help="Page title override")
    parser.add_argument(
        "--index",
        action="store_true",
        help="Generate the cross-retro navigation index instead of a single-retro page.",
    )
    parser.add_argument(
        "--retros-dir",
        default=".retros",
        help="Directory scanned when --index is set (default: .retros)",
    )
    return parser.parse_args(list(argv))


def main(argv: Iterable[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)

    if args.index:
        html_doc = render_index(args.retros_dir)
    else:
        if not args.findings:
            print("ERROR: --findings is required unless --index is set.", file=sys.stderr)
            return 2
        findings_data = _read_json(args.findings)
        summary_md = _read_text(args.summary) if args.summary else None
        html_doc = render_summary(findings_data, summary_md, title=args.title)

    out_dir = os.path.dirname(os.path.abspath(args.out))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(html_doc)
    print("Wrote " + args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
