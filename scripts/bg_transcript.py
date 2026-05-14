"""Background session transcript reader.

See specs/dispatch-routing.md (Core Requirements #4-#6, ACs 11-12).

Parses Claude Code stream-json JSONL transcripts produced by both
``claude -p --output-format stream-json`` (headless mode) and the
``--bg`` session linkScanPath (interactive mode). The format is
identical between the two: one JSON event per line, with ``type``
values including ``result`` (preferred clean-exit text) and
``assistant`` (per-turn message content with text blocks).

Public API:
    parse_outcome(path) -> str
        Preferred name. Returns the ``result`` event text if present,
        else assembled ``assistant`` text blocks. Empty string if the
        file is missing or has no extractable text.

    iter_assistant_text(path, offset=0) -> Generator[tuple[int, str]]
        Streaming reader for heartbeat live-read. Yields
        ``(byte_offset_after_line, text)`` so callers can resume from a
        prior offset and pick up only new assistant text.

    extract_text_from_jsonl(path) -> str
        Backwards-compatibility alias for ``parse_outcome``. Existing
        callers in scripts/pipeline.py import via this name.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Generator, Tuple, Union


def parse_outcome(path: Union[str, Path]) -> str:
    """Return ``type:"result"`` text if present, else assembled assistant text.

    AC-11: prefers ``result`` event text on clean exit.
    AC-12: falls back to concatenated ``assistant`` text blocks on
    timeout / crash where no result was written.
    """
    result_text_parts: list[str] = []
    assistant_text_parts: list[str] = []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                event_type = event.get("type")
                if event_type == "result":
                    result_text = event.get("result", "")
                    if result_text:
                        result_text_parts.append(result_text)
                elif event_type == "assistant":
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        if block.get("type") == "text":
                            text = block.get("text", "")
                            if text:
                                assistant_text_parts.append(text)
    except OSError:
        return ""

    if result_text_parts:
        return "\n".join(result_text_parts)
    return "\n\n".join(assistant_text_parts)


def iter_assistant_text(
    path: Union[str, Path], offset: int = 0
) -> Generator[Tuple[int, str], None, None]:
    """Yield ``(byte_offset_after_line, text)`` for assistant text blocks.

    Resume-from-offset live-read for heartbeat consumers. Skips malformed
    lines and non-assistant events. Returns nothing if the file is missing
    or the offset is past EOF.
    """
    try:
        with open(path, "rb") as f:
            f.seek(offset)
            while True:
                raw = f.readline()
                if not raw:
                    return
                pos = f.tell()
                try:
                    line = raw.decode("utf-8", errors="replace").strip()
                except UnicodeDecodeError:
                    continue
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if event.get("type") != "assistant":
                    continue
                content = event.get("message", {}).get("content", [])
                for block in content:
                    if block.get("type") == "text":
                        text = block.get("text", "")
                        if text:
                            yield pos, text
    except OSError:
        return


extract_text_from_jsonl = parse_outcome
