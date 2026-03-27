"""Tests for session-stop-workflow hook — .claude/ enforcement and workflow surface."""
import importlib.util
import json
import os
import subprocess
import sys
from unittest.mock import patch

import pytest


def _load_hook():
    """Import session-stop-workflow.py as a module."""
    hook_path = os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "session-stop-workflow.py",
    )
    hook_path = os.path.normpath(hook_path)
    spec = importlib.util.spec_from_file_location("session_stop_workflow", hook_path)
    mod = importlib.util.module_from_spec(spec)
    hooks_dir = os.path.dirname(hook_path)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


class TestClaudeDirEnforcement:
    def test_claude_dir_requires_enforcement(self):
        """AC-29: .claude/ files are NOT in non_code_prefixes, so they require enforcement."""
        # Read the source file and check that .claude/ is not in non_code_prefixes
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "session-stop-workflow.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        # Find the non_code_prefixes tuple in the source
        # It should NOT contain ".claude/"
        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "non_code_prefixes":
                        # Extract tuple elements
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert ".claude/" not in elements, (
                                ".claude/ should NOT be in non_code_prefixes — "
                                "process code requires workflow enforcement"
                            )


class TestWorkflowSurface:
    def test_workflow_surface_accepted(self):
        """AC-25/AC-31: 'workflow' is a valid surface in session-stop-workflow."""
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "session-stop-workflow.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "valid_surfaces":
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert "workflow" in elements, (
                                "'workflow' must be in valid_surfaces"
                            )

    def test_workflow_surface_in_pr_gate(self):
        """AC-25: 'workflow' is a valid surface in pr-gate."""
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "pr-gate.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "valid_surfaces":
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert "workflow" in elements, (
                                "'workflow' must be in pr-gate valid_surfaces"
                            )
