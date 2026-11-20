"""The environment hook (`shakedown-safety.py`) and ordinary work.

With an allowlist configured, a listed active environment and no shakedown,
ordinary commands that have nothing to do with PPDS must not be refused,
whatever the hook can or cannot read in them: scripts run through a shell,
``find -exec``, ``xargs``, ``eval``, runners with variables, loops and
conditionals, PowerShell pipelines and script blocks, searches and commit
messages that mention the product name, ``dotnet build`` and ``dotnet test``.
The rule: an invocation the hook cannot fully place is refused in that mode
only when the command text names a switch (``env`` / ``org`` / ``auth`` with
``select`` / ``switch`` / ``use``) or a target option (``--environment``,
``-env``, ``--profile``, ``-p`` in a text that mentions ``ppds``,
``--allowed-env``, a ``PPDS_`` variable that moves the target).

The second half pins the other side of that rule: the same unreadable shapes
are refused when the text names a switch or a target, fully placed
invocations keep their exact checks, the strict reading stays for an
unlisted active env and during a shakedown, and the active environment is
known by its environment's names only, not by its profile's name.

Run: ``pytest tests/hooks/test_shakedown_ordinary_work.py -v``
"""
from __future__ import annotations

import base64
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "shell-tool-payloads.json"
SHAPES = json.loads(FIXTURES.read_text(encoding="utf-8"))

PS = "PowerShell"
BASH = "Bash"

LISTED, UNLISTED, SHAKEDOWN = "listed", "unlisted", "shakedown"


def _encoded(code: str) -> str:
    """``code`` as ``pwsh -EncodedCommand`` takes it."""
    return base64.b64encode(code.encode("utf-16-le")).decode()


def _profiles(active_name="p", env_name="zz-dev"):
    return {"version": 2, "activeProfileIndex": 1, "profiles": [
        {"index": 1, "name": active_name, "environment": {
            "displayName": env_name, "uniqueName": env_name,
            "url": f"https://{env_name}.crm.example.com/"}},
        {"index": 2, "name": "other", "environment": {
            "displayName": "zz-prod", "uniqueName": "zz-prod",
            "url": "https://zz-prod.crm.example.com/"}},
    ]}


@pytest.fixture(scope="module")
def run(tmp_path_factory):
    """Run the hook for (tool, command) in a mode: ``listed`` (allowlist,
    active env on it, no shakedown), ``unlisted`` (active env not on it) or
    ``shakedown`` (active env on it, write-block on). ``profiles`` replaces
    the profile store for one call."""
    base = tmp_path_factory.mktemp("ordinary")
    project = base / "project"
    (project / ".claude" / "state").mkdir(parents=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")
    repo = base / "repo"
    (repo / "src" / "PPDS.Cli").mkdir(parents=True)
    (repo / "src" / "PPDS.Extension").mkdir(parents=True)
    counter = [0]

    def _run(tool, command, mode, profiles=None):
        counter[0] += 1
        config = base / f"cfg{counter[0]}"
        config.mkdir()
        (config / "profiles.json").write_text(json.dumps(profiles or _profiles()), encoding="utf-8")
        payload = json.loads(json.dumps(SHAPES.get(tool, SHAPES["Bash"])))
        payload["tool_name"] = tool
        payload["tool_input"]["command"] = command
        payload["cwd"] = str(repo)
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith("PPDS_")}
        env.update({"PPDS_CONFIG_DIR": str(config), "CLAUDE_PROJECT_DIR": str(project)})
        env["PPDS_SAFE_ENVS"] = "zz-other" if mode == UNLISTED else "zz-dev"
        if mode == SHAKEDOWN:
            env["PPDS_SHAKEDOWN"] = "1"
        r = subprocess.run([sys.executable, str(HOOK_PATH)],
                           input=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                           capture_output=True, timeout=60, env=env)
        return r.returncode, r.stderr.decode("utf-8", errors="replace")

    return _run


# Ordinary work: no PPDS intent. (id, tool, command)
ORDINARY = [
    # Scripts through a shell, and shells fed code.
    ("sh-bash-script", BASH, "bash scripts/verify.sh"),
    ("sh-sh-script", BASH, "sh ./scripts/build.sh --fast"),
    ("sh-bash-c", BASH, "bash -c 'npm run lint && npm test'"),
    ("sh-bash-lc", BASH, 'bash -lc "cd src && make"'),
    ("sh-heredoc-bash", BASH, "bash <<'EOF'\nset -e\nnpm ci\nnpm run build\nEOF"),
    ("sh-heredoc-python", BASH,
     "python - <<'EOF'\nimport json\nprint(json.load(open('package.json'))['version'])\nEOF"),
    ("sh-source", BASH, "source .venv/bin/activate && pytest -q"),
    ("sh-dot-script", BASH, ". ./scripts/vars.sh && echo done"),
    ("sh-node-script", BASH, 'node scripts/check.mjs "$@"'),
    # find -exec, xargs, eval.
    ("sh-find-exec", BASH, "find . -name '*.py' -exec python -m py_compile {} \\;"),
    ("sh-find-exec-grep", BASH, "find src -name '*.csproj' -exec grep -l PPDS {} +"),
    ("sh-find-xargs", BASH, "find . -type f -name '*.md' | xargs grep -n 'TODO'"),
    ("sh-git-xargs", BASH, "git ls-files '*.cs' | xargs wc -l"),
    ("sh-xargs-n", BASH, "xargs -n1 echo < list.txt"),
    ("sh-eval-agent", BASH, 'eval "$(ssh-agent -s)"'),
    ("sh-eval-var", BASH, 'eval "$SETUP_CMD"'),
    # Runners with variables.
    ("sh-timeout-var", BASH, 'timeout "$T" npm test'),
    ("sh-timeout-dotnet", BASH, 'timeout 600 dotnet test PPDS.sln --filter "Category!=Integration" -v q'),
    ("sh-timeout-assign", BASH, "T=300; timeout $T npm run ext:test"),
    ("sh-var-runner", BASH, '"$PYTHON" -m pytest tests/ -q'),
    ("sh-var-program", BASH, "$RUNNER test --verbose"),
    ("sh-env-prefix", BASH, "env FOO=1 npm test"),
    ("sh-env-unset", BASH, "env -u NODE_OPTIONS npm run build"),
    ("sh-nice-dotnet", BASH, "nice -n 10 dotnet build PPDS.sln -v q"),
    ("sh-watch", BASH, "watch -n 5 'git status --short'"),
    ("sh-nohup", BASH, "nohup npm run watch > /dev/null 2>&1 &"),
    # Loops and conditionals.
    ("sh-for-pytest", BASH, 'for f in tests/*.py; do python -m pytest "$f" -q; done'),
    ("sh-for-echo", BASH, 'for p in src/*/; do echo "$p"; done'),
    ("sh-if-file", BASH, "if [ -f package.json ]; then npm test; else echo none; fi"),
    ("sh-while-read", BASH, 'while read -r line; do echo "$line"; done < notes.txt'),
    ("sh-subshell", BASH, "(cd tests && python -m pytest -q)"),
    ("sh-group", BASH, "{ echo a; echo b; } | sort"),
    ("sh-subst", BASH, 'x=$(git rev-parse HEAD); echo "$x"'),
    ("sh-printf-subst", BASH, "printf '%s\\n' \"$(git branch --show-current)\""),
    # Searches and messages that mention the product name.
    ("sh-grep", BASH, 'grep -rn "ppds" src/PPDS.Cli/Commands | head -20'),
    ("sh-rg", BASH, "rg -n 'ppds-mcp-server' docs/"),
    ("sh-awk", BASH, "awk '/ppds/ {print FILENAME\": \"$0}' README.md"),
    ("sh-git-grep", BASH, 'git grep -n "ppds auth" -- docs'),
    ("sh-echo-quoted", BASH, "echo 'ppds data list' > notes.txt"),
    ("sh-commit-possessive", BASH,
     'git commit -m "fix(hooks): ppds\'s environment gate no longer refuses ordinary commands"'),
    ("sh-commit-heredoc", BASH,
     "git commit -m \"$(cat <<'EOF'\ndocs: describe the ppds CLI install\n\nA longer body.\nEOF\n)\""),
    ("sh-commit-am", BASH, 'git commit -am "chore: bump the ppds-mcp-server version"'),
    ("sh-tee", BASH, 'echo "build ppds docs" | tee out.txt'),
    ("sh-ls", BASH, "ls -la src/PPDS.Cli | head"),
    ("sh-cat", BASH, "cat src/PPDS.Cli/PPDS.Cli.csproj"),
    ("sh-sed", BASH, "sed -n 1,40p docs/SAFE-SHAKEDOWN.md"),
    # Builds and tests.
    ("sh-dotnet-build", BASH, "dotnet build PPDS.sln -v q"),
    ("sh-dotnet-test", BASH, 'dotnet test PPDS.sln --filter "Category!=Integration" -v q'),
    ("sh-dotnet-test-project", BASH,
     'dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj -f net10.0 --filter "Category!=Integration"'),
    ("sh-npm-prefix", BASH, "npm run typecheck:all --prefix src/PPDS.Extension"),
    ("sh-cd-npm", BASH, "cd src/PPDS.Extension && npm run lint"),
    ("sh-make", BASH, "make -C build all"),
    ("sh-git-log", BASH, "git log --oneline -5 | cat"),
    ("sh-git-diff", BASH, "git diff --stat origin/main...HEAD"),
    # PowerShell pipelines, script blocks, conditionals and scripts.
    ("ps-foreach-object", PS, "Get-ChildItem -Recurse -Filter *.cs | ForEach-Object { $_.FullName }"),
    ("ps-percent", PS, "Get-ChildItem src | % { $_.Name }"),
    ("ps-where-sort", PS, "Get-Process | Where-Object { $_.CPU -gt 10 } | Sort-Object CPU"),
    ("ps-range-foreach", PS, '1..3 | ForEach-Object { Write-Output "run $_" }'),
    ("ps-foreach-statement", PS,
     "foreach ($f in Get-ChildItem *.json) { Get-Content $f | ConvertFrom-Json | Out-Null }"),
    ("ps-if-test-path", PS, "if (Test-Path package.json) { npm test } else { Write-Host 'none' }"),
    ("ps-if-lastexit", PS, "dotnet build PPDS.sln -v q; if ($LASTEXITCODE -ne 0) { exit 1 }"),
    ("ps-try-catch", PS, "try { npm test } catch { Write-Error $_; exit 1 }"),
    ("ps-pwsh-file", PS, "pwsh -File scripts/Generate-EarlyBoundModels.ps1 -WhatIf"),
    ("ps-pwsh-command", PS, 'pwsh -NoProfile -Command "Get-Date"'),
    ("ps-pwsh-encoded", PS, "pwsh -NoProfile -EncodedCommand " + _encoded("Get-ChildItem | Select-Object -First 3")),
    ("ps-call-var", PS, "& $env:PYTHON -m pytest tests -q"),
    ("ps-call-script", PS, "& ./scripts/build.ps1 -Configuration Release"),
    ("ps-dot-source", PS, ". ./scripts/vars.ps1"),
    ("ps-iex", PS, 'Invoke-Expression "npm test"'),
    ("ps-env-assign", PS, "$env:NODE_ENV = 'test'; npm test"),
    ("ps-start-process", PS, "Start-Process npm -ArgumentList 'test' -Wait"),
    ("ps-select-object", PS, "Get-Content README.md | Select-Object -First 20"),
    ("ps-measure", PS, "Get-ChildItem -Recurse src\\PPDS.Cli -Include *.cs | Measure-Object"),
    ("ps-tee", PS, "npm run ext:test --prefix src/PPDS.Extension 2>&1 | Tee-Object -FilePath out.txt"),
    ("ps-dotnet-version", PS, "$out = dotnet --version; Write-Host $out"),
    ("ps-dotnet-build", PS, "dotnet build PPDS.sln -v q"),
    ("ps-dotnet-test", PS, 'dotnet test PPDS.sln --filter "Category!=Integration" -v q'),
    ("ps-select-string", PS, "Select-String -Path src\\*.cs -Pattern 'ppds'"),
    ("ps-write-host", PS, 'Write-Host "ppds tests passed"'),
    ("ps-rg", PS, 'rg -n "ppds-mcp-server" docs'),
    ("ps-here-string-commit", PS, 'git commit -m @"\nfix: the ppds hook reads both shells\n"@'),
    ("ps-here-string-var", PS, "$msg = @'\ndocs: the ppds CLI\n'@; git commit -m $msg"),
]


def test_ordinary_corpus_is_large_enough():
    assert len(ORDINARY) >= 70
    assert len({case[0] for case in ORDINARY}) == len(ORDINARY)
    assert {tool for _, tool, _ in ORDINARY} == {BASH, PS}


@pytest.mark.parametrize("cid,tool,command", ORDINARY, ids=[c[0] for c in ORDINARY])
def test_ordinary_work_passes_with_a_listed_env(run, cid, tool, command):
    rc, err = run(tool, command, LISTED)
    assert rc == 0, f"{cid}: refused with a listed active env and no shakedown:\n{err}"


@pytest.mark.parametrize("cid,tool,command", ORDINARY[:6], ids=[c[0] for c in ORDINARY[:6]])
def test_ordinary_work_passes_with_a_listed_env_in_the_other_tool(run, cid, tool, command):
    rc, err = run(PS if tool == BASH else BASH, command, LISTED)
    assert rc == 0, err


# The same shapes the hook cannot place, now naming a switch or a target:
# refused with a listed active env. (id, tool, command)
NAMES_TARGET = [
    ("xargs-switch", BASH, "echo env select --environment zz-prod | xargs ppds"),
    ("xargs-org-use", BASH, "printf '%s\\n' org use zz-prod | xargs ppds"),
    ("xargs-auth-switch", BASH, "echo auth switch other | xargs ppds"),
    ("xargs-profile", BASH, "echo data list --profile other | xargs ppds"),
    ("xargs-profile-equals", BASH, "echo data list --profile=other | xargs ppds"),
    ("xargs-short-profile", BASH, "echo data list -p other | xargs ppds"),
    ("xargs-env-colon", BASH, "echo data list -env:zz-prod | xargs ppds"),
    ("var-program-env", BASH, "$P data delete --entity account --environment zz-prod"),
    ("runner-var-switch", BASH, "timeout 5 `echo ppds` env select --environment zz-prod"),
    ("set-dollar-one", BASH, "bash <<< 'set -- ppds; \"$1\" env select --environment zz-prod'"),
    ("here-string-switch", BASH, "env FOO=1 bash <<< '$(echo ppds) env select zz-prod'"),
    ("here-string-profile", BASH, "env FOO=1 bash <<< '$(echo ppds) data delete --profile other'"),
    ("heredoc-switch", BASH, "bash <<'EOF'\n$(echo ppds) env select zz-prod\nEOF"),
    ("pipe-to-bash", BASH, "echo '$(echo ppds) env select zz-prod' | env X=1 bash"),
    ("bash-c-var", BASH, "env X=1 \"$SHELL\" -c '$(echo ppds) org select zz-prod'"),
    # A shell fed code after a prefix or assignments, its name literal,
    # quoted, from a variable or from a substitution.
    ("prefix-heredoc", BASH, "env FOO=1 bash <<'EOF'\n$(echo ppds) env select zz-prod\nEOF"),
    ("assign-heredoc", BASH, "FOO=1 bash <<'EOF'\n$(echo ppds) env select zz-prod\nEOF"),
    ("timeout-quoted-c", BASH, "timeout 5 \"bash\" -c '$(echo ppds) env select zz-prod'"),
    ("assign-c", BASH, "X=1 bash -c '$(echo ppds) env select zz-prod'"),
    ("nice-here-string", BASH, "nice -n 5 sh <<< '$(echo ppds) env select zz-prod'"),
    ("var-shell-here-string", BASH, "env X=1 $SH <<< '$(echo ppds) env select zz-prod'"),
    ("subst-shell-heredoc", BASH, "$(which bash) <<'EOF'\n$(echo ppds) env select zz-prod\nEOF"),
    ("var-shell-pipe", BASH, "echo '$(echo ppds) org select zz-prod' | sudo -E $SH"),
    # The switch or target only inside an encoded PowerShell command.
    ("encoded-switch", BASH,
     "env FOO=1 pwsh -NoProfile -EncodedCommand " + _encoded("$a = @('org', 'select', 'zz-prod'); ppds @a")),
    ("encoded-profile", PS,
     "pwsh -NoProfile -EncodedCommand " + _encoded("$a = @('data', 'list', '--profile', 'other'); ppds @a")),
    ("encoded-twice", PS, "pwsh -EncodedCommand " + _encoded(
        "pwsh -EncodedCommand " + _encoded("ppds-mcp-server (echo --read-only) --environment zz-prod"))),
    ("sudo-pwsh-c", BASH, "sudo -u u pwsh -c '& (\"pp\"+\"ds\") env select zz-prod'"),
    ("response-file-switch", BASH, "$P @args.rsp  # env select"),
    ("dyn-arg-profile", BASH, "ppds data delete --entity account -p $PROFILE"),
    ("dyn-arg-allowed-env", BASH, "ppds-mcp-server --allowed-env $E"),
    ("quoted-switch-words", BASH, "echo 'e\"n\"v sel''ect zz-prod' | xargs ppds"),
    ("escaped-option", BASH, "echo data list \\-\\-profile other | xargs ppds"),
    ("ps-iex-switch", PS, "Invoke-Expression ('pp' + 'ds env select zz-prod')"),
    ("ps-call-var-profile", PS, "& $exe data delete --entity account --profile other"),
    ("ps-scriptblock-switch", PS, "$c = 'env select zz-prod'; Invoke-Expression \"ppds $c\""),
    ("unplaced-mention-profile", BASH, 'git commit -m "ppds\'s data list --profile other"'),
]


@pytest.mark.parametrize("cid,tool,command", NAMES_TARGET, ids=[c[0] for c in NAMES_TARGET])
def test_unplaced_invocation_naming_a_target_is_refused_with_a_listed_env(run, cid, tool, command):
    rc, err = run(tool, command, LISTED)
    assert rc == 2, f"{cid}: passed with a listed active env:\n{err}"


@pytest.mark.parametrize("cid,tool,command", NAMES_TARGET, ids=[c[0] for c in NAMES_TARGET])
def test_unplaced_invocation_naming_a_target_is_refused_in_the_strict_modes(run, cid, tool, command):
    for mode in (UNLISTED, SHAKEDOWN):
        rc, err = run(tool, command, mode)
        assert rc == 2, f"{cid} ({mode}): passed:\n{err}"


# Fully placed invocations keep their exact checks with a listed active env.
PLACED_REFUSED = [
    ("switch-unlisted", "ppds env select zz-prod"),
    ("switch-unlisted-option", "ppds env select --environment zz-prod"),
    ("switch-empty-equals", "ppds env select --environment= zz-prod"),
    ("switch-empty-colon", "ppds env select -env: zz-prod"),
    ("switch-no-target", "ppds env select"),
    ("org-select-unlisted", "ppds org select zz-prod"),
    ("auth-select-unlisted", "ppds auth select --name other"),
    ("per-command-env", "ppds data delete --entity account --environment zz-prod"),
    ("per-command-profile", "ppds data delete --entity account --profile other"),
    ("bundle", "ppds data delete --entity account -vp other"),
    ("mcp-allowed-env", "ppds-mcp-server --allowed-env zz-prod"),
    ("response-file", "ppds @args.rsp"),
    ("response-file-after-path", "ppds env @args.rsp"),
    ("config-dir-variable", "PPDS_CONFIG_DIR=/tmp/x ppds data list"),
]


@pytest.mark.parametrize("cid,command", PLACED_REFUSED, ids=[c[0] for c in PLACED_REFUSED])
def test_placed_target_checks_stay_exact_with_a_listed_env(run, cid, command):
    for tool in (BASH, PS):
        rc, err = run(tool, command, LISTED)
        assert rc == 2, f"{cid} ({tool}): passed:\n{err}"


PLACED_PASSES = [
    "ppds env select zz-dev",
    "ppds env select --environment=zz-dev",
    "ppds data delete --entity account --environment zz-dev",
    "ppds auth select --name p",
    "ppds-mcp-server --allowed-env zz-dev",
]


@pytest.mark.parametrize("command", PLACED_PASSES)
def test_placed_listed_targets_pass_with_a_listed_env(run, command):
    for tool in (BASH, PS):
        rc, err = run(tool, command, LISTED)
        assert rc == 0, err


# What the hook cannot place still refuses when the active env is not listed,
# and during a shakedown, with no switch or target in the text.
STRICT = [
    (BASH, "echo data list | xargs ppds"),
    (BASH, "ppds data delete --entity $E"),
    (BASH, "$P @args.rsp"),
    (BASH, 'git commit -m "ppds\'s hook"'),
    (BASH, "bash scripts/verify.sh"),
    (PS, "Get-ChildItem | ForEach-Object { $_.Name }"),
]


@pytest.mark.parametrize("tool,command", STRICT)
def test_unplaced_invocation_is_refused_with_an_unlisted_env(run, tool, command):
    rc, err = run(tool, command, UNLISTED)
    assert rc == 2, err


@pytest.mark.parametrize("tool,command", STRICT)
def test_unplaced_invocation_is_refused_during_a_shakedown(run, tool, command):
    rc, err = run(tool, command, SHAKEDOWN)
    assert rc == 2, err


@pytest.mark.parametrize("tool,command", STRICT)
def test_unplaced_invocation_with_no_target_passes_with_a_listed_env(run, tool, command):
    rc, err = run(tool, command, LISTED)
    assert rc == 0, err


# The active environment is known by its environment's names, not its
# profile's name: a profile named like a listed env that holds an unlisted
# one does not pass.
def test_profile_named_like_a_listed_env_does_not_pass(run):
    store = _profiles(active_name="zz-dev", env_name="zz-unlisted")
    for tool in (BASH, PS):
        rc, err = run(tool, "ppds data list", LISTED, profiles=store)
        assert rc == 2, err
        assert "zz-unlisted" in err


def test_profile_whose_environment_is_listed_passes(run):
    store = _profiles(active_name="anything", env_name="zz-dev")
    rc, err = run(BASH, "ppds data list", LISTED, profiles=store)
    assert rc == 0, err
