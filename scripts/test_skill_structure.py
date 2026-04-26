#!/usr/bin/env python3
"""Tests for two-file skill split structure (PR-3, AC-159 through AC-162)."""
from __future__ import annotations
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SKILLS = REPO_ROOT / '.claude' / 'skills'
LINE_CAP = 150


def _line_count(path):
    with open(path, encoding='utf-8') as f:
        text = f.read()
    return text.count(chr(10)) + (0 if text.endswith(chr(10)) else 1)


class TestSkillStructure(unittest.TestCase):

    def test_release_skill_line_count(self):
        path = SKILLS / 'release' / 'SKILL.md'
        ref = SKILLS / 'release' / 'REFERENCE.md'
        self.assertTrue(path.exists())
        self.assertTrue(ref.exists())
        n = _line_count(path)
        self.assertLessEqual(n, LINE_CAP, f'release SKILL.md is {n} lines; cap is {LINE_CAP}')
        text = path.read_text(encoding='utf-8')
        self.assertIn('Read REFERENCE.md', text)

    def test_backlog_skill_line_count(self):
        path = SKILLS / 'backlog' / 'SKILL.md'
        ref = SKILLS / 'backlog' / 'REFERENCE.md'
        self.assertTrue(path.exists())
        self.assertTrue(ref.exists())
        n = _line_count(path)
        self.assertLessEqual(n, LINE_CAP, f'backlog SKILL.md is {n} lines; cap is {LINE_CAP}')
        text = path.read_text(encoding='utf-8')
        self.assertIn('Read REFERENCE.md', text)

    def test_retro_skill_line_count(self):
        path = SKILLS / 'retro' / 'SKILL.md'
        ref = SKILLS / 'retro' / 'REFERENCE.md'
        self.assertTrue(path.exists())
        self.assertTrue(ref.exists())
        n = _line_count(path)
        self.assertLessEqual(n, LINE_CAP, f'retro SKILL.md is {n} lines; cap is {LINE_CAP}')
        text = path.read_text(encoding='utf-8')
        self.assertIn('Read REFERENCE.md', text)

    def test_two_file_pattern_doc_exists(self):
        path = SKILLS / 'TWO-FILE-PATTERN.md'
        self.assertTrue(path.exists())
        text = path.read_text(encoding='utf-8')
        self.assertIn('SKILL.md', text)
        self.assertIn('REFERENCE.md', text)
        self.assertIn('Read REFERENCE.md', text)
        self.assertIn('release', text.lower())


if __name__ == '__main__':
    unittest.main()
