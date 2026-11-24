"""The fixed list of defects the mutation check re-introduces into a scratch copy of the hook.

Each entry: name -> list of (file under .claude/hooks, exact old text, new text). The list is the one
the independent review of these tests used at a39e7e68 (39 defects). A defect whose old text no longer
occurs exactly once in the current hook is reported as "not applicable" (the code moved on); it is
not silently skipped.
"""
H = "shakedown-safety.py"
P = "_shellparse.py"
L = "shakedown-safety-launcher.py"

M = {}

M["m01_colon_delim"] = [(H, 'r"^(-{1,2}[^=:]*)[=:](.*)$"', 'r"^(-{1,2}[^=]*)[=](.*)$"')]
M["m02a_bare_start_shakedown"] = [(H,
    '        return True, "ppds with no arguments (starts the interactive TUI; treated as a write)"',
    '        return False, ""')]
M["m02b_bare_start_envgate"] = [(H,
    '    args = list(argv[1:])\n\n    def refuse(why):',
    '    args = list(argv[1:])\n    if not args and prog == "ppds":\n        return True, ""\n\n    def refuse(why):')]
M["m03_sql_leading_dash_dropped"] = [(H,
    '    word = sql_write_word("\\n".join(args))',
    '    word = sql_write_word("\\n".join(a for a in args if not a.startswith("-")))')]
M["m04_sql_word_boundary"] = [(H,
    '    return next((w.lower() for w in sql_words(text) if w.lower() in _SQL_WRITE_WORDS), None)',
    '    m = re.search(r"\\b(" + "|".join(sorted(_SQL_WRITE_WORDS)) + r")\\b", text, re.IGNORECASE)\n    return m.group(1).lower() if m else None')]
M["m05_dry_run_anywhere"] = [(H,
    '    dry = False\n    for k, arg in enumerate(args):',
    '    return "--dry-run" not in args\n    dry = False\n    for k, arg in enumerate(args):')]
M["m06_read_only_anywhere"] = [(H,
    '    i, read_only = 0, False\n    while i < len(args):',
    '    return "--read-only" in args\n    i, read_only = 0, False\n    while i < len(args):')]
M["m07_ignore_environment_option"] = [(H,
    '        if name in _ENV_OPTIONS:\n            if not target_listed(value, allowlist):',
    '        if name in _ENV_OPTIONS:\n            if False:')]
M["m08_profile_name_as_env"] = [
    (H, '        if not env_matches_allowlist(_env_candidates(p.get("environment")), allowlist):',
        '        if not env_matches_allowlist(_env_candidates(p.get("environment")) + [str(p.get("name") or "")], allowlist):'),
    (H, '    # The profile\'s own name is not a name of its environment: a profile\n    # named like a listed environment may hold an unlisted one.\n    return candidates',
        '    if isinstance(active.get("name"), str):\n        candidates.append(active["name"])\n    return candidates'),
]
M["m09_no_shell_feed_after_prefix"] = [
    (P, '        lead = next((w for w in self.words if not is_assignment(w)), None)\n        if lead is None:\n            return True\n        name = lead.text.lower() if not lead.dynamic else None\n        return name in COMMAND_PREFIXES or name in OPAQUE_PREFIXES',
        '        return False'),
    (P, '        code = first_code or self.shell is not None', '        code = first_code'),
]
M["m10_unplaced_switch_passes_when_listed"] = [(H,
    '        names_target = shakedown_active or text_names_target(command if parsed is not None else raw)',
    '        names_target = shakedown_active')]
M["m11_auth_select_passes"] = [(H,
    '    if path == ("auth", "select"):\n        targets, problem = _switch_targets(rest, {"--index", "-i", "--name", "-n"}, set("in"))',
    '    if path == ("auth", "select"):\n        return True, ""\n        targets, problem = _switch_targets(rest, {"--index", "-i", "--name", "-n"}, set("in"))')]
M["m12_response_file_passes_both_gates"] = [
    (H, '    response = next((arg for arg in args if arg.startswith("@")), None)\n    if response is not None:\n        # A response file written',
        '    response = None\n    if response is not None:\n        # A response file written'),
    (H, '    response = next((arg for arg in argv[1:] if arg.startswith("@")), None)\n    if response is not None:\n        return True, f"{_shown([response])} (a response file',
        '    response = None\n    if response is not None:\n        return True, f"{_shown([response])} (a response file'),
]
M["m12a_response_file_passes_envgate"] = [M["m12_response_file_passes_both_gates"][0]]
M["m12b_response_file_passes_writeblock"] = [M["m12_response_file_passes_both_gates"][1]]
M["m13_cap_removed_runs_past_budget"] = [
    (H, '        if count >= _DOTNET_MAX_OCCURRENCES:\n            out.extend(_unknown([UNREADABLE]))\n            break\n        _tick(deadline)\n        prog = match.group(1)',
        '        prog = match.group(1)'),
]
M["m13b_caps_raised_deadline_kept"] = [
    (H, '_DOTNET_MAX_OCCURRENCES = 500', '_DOTNET_MAX_OCCURRENCES = 10**9'),
    (H, '_MAX_PROGRAM_WORDS = 500', '_MAX_PROGRAM_WORDS = 10**9'),
    (H, '_MAX_INVOCATIONS = 2_000', '_MAX_INVOCATIONS = 10**9'),
    (H, '_MAX_MENTIONS = 1_000', '_MAX_MENTIONS = 10**9'),
]
M["m14_ignore_source_target_env"] = [
    (H, '_ENV_OPTIONS = {"--environment", "-env", "-e", "--env", "--source-env", "-se", "--target-env", "-te"}',
        '_ENV_OPTIONS = {"--environment", "-env", "-e", "--env"}'),
    (H, '_PROFILE_OPTIONS = {"--profile", "-p", "--source-profile", "-sp", "--target-profile", "-tp"}',
        '_PROFILE_OPTIONS = {"--profile", "-p"}'),
    (H, '_TARGET_LETTERS = set("ep")', '_TARGET_LETTERS = set()'),
]
M["m15_ignore_target_variables"] = [(H,
    '    names = _ppds_vars(command)\n    if not names:\n        return ""',
    '    return ""\n    names = _ppds_vars(command)\n    if not names:\n        return ""')]
M["m15b_environment_url_var_safe"] = [(H,
    '_SAFE_PPDS_VARS = {"ppds_shakedown",', '_SAFE_PPDS_VARS = {"ppds_environment_url", "ppds_client_id", "ppds_client_secret", "ppds_tenant_id", "ppds_shakedown",')]
M["m16_ignore_profile_option"] = [(H,
    '            why = profile_target_problem(store, value, allowlist)\n            if why:\n                return f"{tok} {value}: {why}", False',
    '            why = ""\n            if why:\n                return f"{tok} {value}: {why}", False')]
M["m17_data_delete_classified_read"] = [(H, 'data: analyze=r copy=w delete=w', 'data: analyze=r copy=w delete=r')]
M["m18_help_flag_anywhere"] = [(H,
    '    if nxt is not None and nxt in _HELP_FLAGS:\n        return False, ""',
    '    if any(a in _HELP_FLAGS for a in rest):\n        return False, ""')]
M["m19_version_update_passes"] = [(H,
    '        if rest in ([], ["--check"]):\n            return False, ""\n        return True, f"{shown} (passes only alone',
    '        return False, ""\n        return True, f"{shown} (passes only alone')]
M["m20_env_config_write_passes"] = [(H,
    '    if path == ("env", "config"):\n        if rest in ([], ["--list"], ["--show"], ["-s"]):',
    '    if path in (("env", "config"), ("env", "type")):\n        return True\n    if path == ("env", "config"):\n        if rest in ([], ["--list"], ["--show"], ["-s"]):')]
M["m21_encoded_command_not_decoded"] = [(P,
    '        self.code_arg = True\n        self._scan(decoded, self.depth + 1, True, dialects=("ps",), wrapped=True, kind="code")',
    '        self.code_arg = True')]
M["m22_mention_accounting_off"] = [(H,
    '    if not mentions:\n        return False\n    if len(mentions) > _MAX_MENTIONS or not result.complete or result.top is None:',
    '    return False\n    if len(mentions) > _MAX_MENTIONS or not result.complete or result.top is None:')]
M["m23_unknown_active_passes"] = [(H,
    '    def active_check():\n        if env_matches_allowlist(active, allowlist) and all(',
    '    def active_check():\n        if not active:\n            return True, ""\n        if env_matches_allowlist(active, allowlist) and all(')]
M["m24_env_select_passes_without_allowlist"] = [(H,
    '        if allowlist_configured:\n            return False, ""\n        return True, (f"{shown} (with no safe-env allowlist',
    '        return False, ""\n        return True, (f"{shown} (with no safe-env allowlist')]
M["m25_sentinel_ignored"] = [(H, '    sentinel_active = check_sentinel_active()', '    sentinel_active = False')]
M["m26_dotnet_projects_unknown"] = [(H,
    '_PPDS_PROJECTS = {"ppds.cli": "ppds", "ppds.mcp": "ppds-mcp-server"}', '_PPDS_PROJECTS = {}')]
M["m27_runtime_program_not_guessed"] = [(H,
    '                if word.dynamic or prog is None or (word.glob and word.text != "?"):',
    '                if False:')]
M["m28_switch_to_unlisted_passes"] = [(H,
    '        bad = next((t for t in targets if not target_listed(t, allowlist)), None)',
    '        bad = None')]
M["m29_mcp_targets_ignored"] = [(H,
    '        why = _mcp_target_problem(args, allowlist, store)\n        return refuse(why) if why else active_check()',
    '        return active_check()')]
M["m30_watchdog_and_deadline_off"] = [
    (H, '    if deadline is not None and time.monotonic() > deadline:\n        raise _Deadline()', '    return'),
    (L, '_TIMEOUT_SECONDS = 5.0', '_TIMEOUT_SECONDS = 600.0'),
    (P, '        if (self.work & 0x3FF) == 0 and time.monotonic() > self.deadline:', '        if False:'),
    (H, '_DOTNET_MAX_OCCURRENCES = 500', '_DOTNET_MAX_OCCURRENCES = 10**9'),
    (H, '_MAX_PROGRAM_WORDS = 500', '_MAX_PROGRAM_WORDS = 10**9'),
    (H, '_MAX_INVOCATIONS = 2_000', '_MAX_INVOCATIONS = 10**9'),
    (H, '_MAX_MENTIONS = 1_000', '_MAX_MENTIONS = 10**9'),
]
M["m31_only_first_invocation_checked"] = [(H,
    '        invocations = find_ppds_invocations(command, tool_name, cwd, deadline) if command.strip() else []',
    '        invocations = (find_ppds_invocations(command, tool_name, cwd, deadline) if command.strip() else [])[:1]')]
M["m33_only_the_tools_own_dialect_read"] = [(H,
    '        for dialect in ("posix", "ps"):\n            out.extend(_read_invocations(command, dialect, cwd, mentions, deadline))',
    '        for dialect in (("ps",) if tool_name == "PowerShell" else ("posix",)):\n            out.extend(_read_invocations(command, dialect, cwd, mentions, deadline))')]
M["m34_five_write_paths_classified_read"] = [
    (H, 'flows: disable=w enable=w get=r', 'flows: disable=r enable=r get=r'),
    (H, 'data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=w users=r',
        'data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=r users=r'),
    (H, 'metadata entity: add-statusreason=w create=w delete=w list-statusreasons=r remove-statusreason=w update=w update-statusreason=w',
        'metadata entity: add-statusreason=w create=w delete=r list-statusreasons=r remove-statusreason=r update=w update-statusreason=w'),
]
M["m35_three_write_paths_classified_read"] = [
    (H, 'flows: disable=w enable=w get=r', 'flows: disable=r enable=w get=r'),
    (H, 'data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=w users=r',
        'data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=r users=r'),
    (H, 'metadata entity: add-statusreason=w create=w delete=w list-statusreasons=r',
        'metadata entity: add-statusreason=w create=w delete=r list-statusreasons=r'),
]
