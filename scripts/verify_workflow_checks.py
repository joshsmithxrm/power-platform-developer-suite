"""Verify workflow checks — importable validation functions for .claude/ and scripts/."""
import json
import os
import re


def check_skill_frontmatter(skill_path: str) -> list[str]:
    """Check 4: Validate skill YAML frontmatter has name and description.

    Returns list of error messages (empty = pass).
    """
    errors = []
    try:
        with open(skill_path, "r", encoding="utf-8") as f:
            content = f.read()
    except OSError as e:
        return [f"Cannot read {skill_path}: {e}"]

    # Extract frontmatter between --- delimiters
    parts = content.split("---", 2)
    if len(parts) < 3:
        return [f"{skill_path}: No YAML frontmatter found (missing --- delimiters)"]

    frontmatter = parts[1].strip()
    # Simple YAML parsing for name and description
    fields = {}
    for line in frontmatter.split("\n"):
        if ":" in line:
            key, _, value = line.partition(":")
            fields[key.strip()] = value.strip()

    if not fields.get("name"):
        errors.append(f"{skill_path}: Missing or empty 'name' field in frontmatter")
    if not fields.get("description"):
        errors.append(f"{skill_path}: Missing or empty 'description' field in frontmatter")

    return errors


def check_agent_frontmatter(agent_path: str) -> list[str]:
    """Check 5: Validate agent YAML frontmatter has valid tools.

    Returns list of error messages (empty = pass).
    """
    VALID_TOOLS = {
        "Read", "Edit", "Write", "Glob", "Grep", "Bash",
        "Agent", "WebSearch", "WebFetch", "NotebookEdit",
    }
    errors = []
    try:
        with open(agent_path, "r", encoding="utf-8") as f:
            content = f.read()
    except OSError as e:
        return [f"Cannot read {agent_path}: {e}"]

    parts = content.split("---", 2)
    if len(parts) < 3:
        return [f"{agent_path}: No YAML frontmatter found"]

    frontmatter = parts[1].strip()

    # Parse tools list from YAML
    tools = []
    in_tools = False
    for line in frontmatter.split("\n"):
        stripped = line.strip()
        if stripped.startswith("tools:"):
            in_tools = True
            # Check for inline list
            after = stripped[len("tools:"):].strip()
            if after.startswith("["):
                # Inline list: tools: [Read, Grep]
                tools = [t.strip() for t in after.strip("[]").split(",") if t.strip()]
                in_tools = False
            continue
        if in_tools:
            if stripped.startswith("- "):
                tool_name = stripped[2:].strip()
                tools.append(tool_name)
            elif stripped and not stripped.startswith("#"):
                in_tools = False

    if not tools:
        errors.append(f"{agent_path}: No 'tools' list found in frontmatter")
        return errors

    for tool in tools:
        # Allow Bash with patterns like Bash(git diff:*)
        base_tool = tool.split("(")[0] if "(" in tool else tool
        if base_tool not in VALID_TOOLS:
            errors.append(f"{agent_path}: Invalid tool '{tool}' — not in known valid set")

    return errors


def check_skill_file_references(skill_path: str, repo_root: str) -> list[str]:
    """Check 6: Find file path references in skill markdown and verify they exist.

    Returns list of error messages (empty = pass).
    """
    errors = []
    try:
        with open(skill_path, "r", encoding="utf-8") as f:
            content = f.read()
    except OSError as e:
        return [f"Cannot read {skill_path}: {e}"]

    # Match relative paths like ./foo, ../bar, but not in code blocks that are templates
    # Focus on markdown link targets: [text](path) where path starts with ./ or ../
    link_pattern = re.compile(r'\[.*?\]\((\.\.?/[^)]+)\)')

    for match in link_pattern.finditer(content):
        ref_path = match.group(1)
        # Resolve relative to the skill file's directory
        skill_dir = os.path.dirname(skill_path)
        resolved = os.path.normpath(os.path.join(skill_dir, ref_path))
        if not os.path.exists(resolved):
            errors.append(f"{skill_path}: Dead link to '{ref_path}' (resolved: {resolved})")

    return errors


def check_retro_store_schema(store_path: str) -> list[str]:
    """Check 7: Validate .retros/summary.json schema.

    Returns list of error messages (empty = pass).
    Missing file is NOT an error — the store is optional.
    """
    if not os.path.exists(store_path):
        return []  # Missing store is OK

    errors = []
    try:
        with open(store_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        return [f"{store_path}: Invalid JSON — {e}"]
    except OSError as e:
        return [f"{store_path}: Cannot read — {e}"]

    required_keys = ["schema_version", "last_updated", "total_retros", "findings_by_category", "metrics"]
    for key in required_keys:
        if key not in data:
            errors.append(f"{store_path}: Missing required key '{key}'")

    if data.get("schema_version") != 1:
        errors.append(
            f"{store_path}: schema_version is {data.get('schema_version')}, expected 1 — "
            "rebuild the store"
        )

    return errors
