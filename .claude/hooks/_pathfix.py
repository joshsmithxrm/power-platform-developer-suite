"""Normalize MSYS-style paths to Windows-native paths for Python on Windows.

Defense-in-depth for Git Bash (MSYS) environments where CLAUDE_PROJECT_DIR
may contain /c/VS/... style paths that Python interprets as C:\\c\\VS\\...
"""
import os
import re
import sys

_MSYS_DRIVE_RE = re.compile(r"^/([a-zA-Z])/(.*)")


def normalize_msys_path(path):
    """Convert MSYS /c/... to C:/... on Windows. No-op elsewhere."""
    if sys.platform == "win32":
        m = _MSYS_DRIVE_RE.match(path)
        if m:
            return f"{m.group(1).upper()}:/{m.group(2)}"
    return path


def get_project_dir():
    """Get CLAUDE_PROJECT_DIR as a platform-native path."""
    return normalize_msys_path(os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd()))
