"""Tests for the command-text guard (`.claude/hooks/command-text-guard.py`).

The guard is not registered in the checked-in settings; the maintainer
installs it. These tests pin what it refuses and what it lets through, for
commands issued through the Bash tool and the PowerShell tool, including
commands wrapped in another shell, a pipeline or a substitution.

Run: ``pytest tests/hooks/test_command_text_guard.py -v``
"""
from __future__ import annotations

import base64
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

HOOK_PATH = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "command-text-guard.py"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "shell-tool-payloads.json"


def _load():
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("command_text_guard", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


guard = _load()


def _payload(tool: str, command: str) -> dict:
    """A hook payload in the shape recorded from Claude Code for ``tool``."""
    shapes = json.loads(FIXTURES.read_text(encoding="utf-8"))
    payload = json.loads(json.dumps(shapes[tool]))
    payload["tool_input"]["command"] = command
    return payload


def _run(stdin: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(HOOK_PATH)], input=stdin,
                          capture_output=True, text=True, timeout=30)


B, P = "Bash", "PowerShell"
_ENC = base64.b64encode("git push --force".encode("utf-16-le")).decode()

# (tool, command) the guard must refuse.
REFUSE = [
    # tags
    (B, "git tag v2.0.0"), (P, "git tag v2.0.0"),
    (B, "git tag -a v2 -m release"), (P, "git tag -s v2 -m release"),
    (B, "git tag -d v1"), (P, "git tag --delete v1"),
    (B, "git tag -f v1"), (B, "git tag -l -d v1"),
    (B, "git push --tags"), (P, "git push origin --tags"),
    (B, "git push --follow-tags"), (P, "git push --follow-tags origin main"),
    (B, "git push origin v2.0.0"), (P, "git push origin Cli-v2.0.0"),
    (B, "git push origin refs/tags/x"), (P, "git push origin HEAD:refs/tags/x"),
    (B, "git push origin :refs/tags/v1"), (B, "git push --delete origin v1"),
    (B, "git update-ref refs/tags/v1 HEAD"), (P, "git update-ref -d refs/tags/v1"),
    (B, "gh release create v2"), (P, "gh release create v2 --notes x"),
    (B, "gh release delete v1 --cleanup-tag"), (B, "gh release upload v1 a.zip"),
    (B, "gh release edit v1 --draft=false"),
    # package publishing
    (B, "dotnet nuget push out/x.nupkg -k k -s https://api.nuget.org/v3/index.json"),
    (P, "dotnet nuget push out\\x.nupkg"), (B, "nuget push x.nupkg"), (P, "nuget.exe push x.nupkg"),
    (B, "dotnet nuget delete PPDS.Cli 1.0.0"),
    (B, "npm publish"), (P, "npm publish --access public"), (B, "pnpm publish"),
    (B, "yarn npm publish"), (B, "npm unpublish x@1.0.0"), (B, "npm dist-tag add x@1 latest"),
    (B, "npx vsce publish"), (P, "npx @vscode/vsce@latest publish -p x"), (B, "vsce publish --pre-release"),
    (B, "ovsx publish x.vsix"), (B, "npm run vsce:publish --prefix src/PPDS.Extension"),
    (P, "Publish-Module -Path ./m -NuGetApiKey x"), (P, "Publish-Script -Path s.ps1"),
    (P, "Publish-PSResource -Path ./m"), (P, "PowerShellGet\\Publish-Module -Name x"),
    (P, "publish-module -name x"),
    # workflow dispatch
    (B, "gh workflow run extension-publish.yml"), (P, "gh workflow run publish-nuget.yml --ref main"),
    (B, "gh workflow run plugin-signing-compatibility.yml"), (B, "gh workflow run 'Publish Extension'"),
    (B, "gh workflow run 12345"), (B, "gh workflow run"), (B, "gh run rerun 123"),
    # gh api writes
    (B, "gh api -X POST repos/o/r/issues"), (P, "gh api --method PATCH repos/o/r"),
    (B, "gh api -XDELETE repos/o/r/git/refs/tags/v1"), (B, "gh api --method=PUT repos/o/r/pulls/1/merge"),
    (B, "gh api repos/o/r/issues -f title=x"), (P, "gh api repos/o/r/issues -F body=@b.md"),
    (B, "gh api repos/o/r/git/refs --raw-field ref=refs/tags/v1"),
    (B, "gh api repos/o/r/dispatches --input body.json"), (B, "gh api graphql -f query=x"),
    (B, "gh api -X GET -f per_page=100 repos/o/r/issues"),
    # gh pr merge
    (B, "gh pr merge 12"), (P, "gh pr merge 12 --squash"), (B, "gh pr merge 12 --match-head-commit abc"),
    (B, "gh pr merge 12 --merge --match-head-commit abc"), (B, "gh pr merge 12 --rebase --match-head-commit a"),
    (B, "gh pr merge 1 --squash --match-head-commit a --admin"), (P, "gh pr merge --admin -s --match-head-commit a 1"),
    (B, "gh pr merge 1 --squash=false --match-head-commit a"), (B, "gh pr merge 1 --squash --match-head-commit"),
    # hooks
    (B, "git commit --no-verify -m x"), (P, "git commit --no-verify -m x"), (B, "git commit -n -m x"),
    (P, "git commit -n -m x"), (B, "git commit -anm x"), (B, "git commit -am x -n"), (B, "git commit --no-veri -m x"),
    (B, "git push --no-verify"), (B, "git merge --no-verify main"),
    (B, "git -c core.hooksPath=/dev/null commit -m x"), (P, "git -c core.hooksPath=NUL commit -m x"),
    (B, "git config core.hooksPath .nohooks"), (P, "git config --global core.HooksPath x"),
    (B, "GIT_CONFIG_PARAMETERS=\"'core.hooksPath=x'\" git commit -m x"),
    (P, "$env:GIT_CONFIG_COUNT=1; $env:GIT_CONFIG_KEY_0='core.hookspath'; git commit -m x"),
    (B, "git -c alias.p=push p -f"), (B, "git config alias.pf 'push --force'"),
    (B, "gh alias set m 'pr merge --admin'"), (P, "Set-Alias g git"), (B, "alias gp='git push --force'"),
    # force push, reset --hard, clean
    (B, "git push --force"), (P, "git push --force"), (B, "git push -f origin main"), (P, "git push -f"),
    (B, "git push origin +main"), (P, "git push origin +HEAD:main"), (B, "git push --force-with-lease"),
    (P, "git push --force-with-lease=main:abc"), (B, "git push --force-if-includes"), (B, "git push -uf origin x"),
    (B, "git push --mirror"), (P, "git push --fo"), (B, "git push origin main --force"), (B, "git push --prune origin"),
    (B, "git -C ../other push -f"), (P, "git --no-pager push -f"),
    (B, "git reset --hard"), (P, "git reset --hard HEAD~1"), (B, "git reset --har origin/main"),
    (B, "git clean -fdx"), (P, "git clean -fd"), (B, "git clean -n"),
    # spellings the shells accept
    (P, "GIT push -f"), (P, "git.exe push -f"), (P, "& git push -f"), (P, "& 'C:\\Program Files\\Git\\cmd\\git.exe' push -f"),
    (P, "g`it push -f"), (P, "git push `\n  --force"), (B, "git push \\\n  --force"), (B, "g\\it push -f"),
    (B, "g''it push -f"), (P, "git push origin main --% -f"), (P, "git push \u2013f"),
    (P, "git push origin main ``\n--force"),
    # wrapped forms
    (P, "pwsh -NoProfile -Command \"git push -f\""), (P, "powershell -Command \"git push -f\""),
    (B, "pwsh -c 'git tag v1'"), (B, "bash -c 'git push --force'"), (P, "bash -lc \"git push --force\""),
    (B, "sh -c \"gh release create v1\""), (P, "cmd /c \"git push -f\""), (P, "cmd /c git push -f"),
    (P, "cmd.exe /c g^it push -f"), (P, f"pwsh -EncodedCommand {_ENC}"), (P, "pwsh -enc !!notbase64!!"),
    (B, "echo x | xargs git push -f"), (P, "Write-Output x | git push -f"),
    (B, "git status && git push -f"), (P, "git status; git push -f"), (P, "git status || git push -f"),
    (B, "git status; $(git push -f)"), (B, "echo `git push -f`"), (P, "Write-Output $(git push -f)"),
    (P, "& { git push -f }"), (P, "Invoke-Expression 'git push -f'"), (P, "iex \"git push -f\""),
    (B, "eval 'git push -f'"), (P, "Start-Process git -ArgumentList 'push','-f'"),
    (P, "$g='git'; & $g push -f"), (B, "$GIT push -f"), (B, "g=git; $g push --force"),
    (B, "(cd x && git push -f)"), (P, "if ($true) { git push -f }"), (B, "if true; then git push -f; fi"),
    (B, "cat <<'EOF' | bash\ngit push -f\nEOF"), (B, "env GIT_TRACE=1 git push -f"), (B, "sudo git push -f"),
    (B, "python -c \"import os; os.system('git push -f')\""), (B, "node -e \"require('child_process').execSync('npm publish')\""),
    # arguments only known at run time
    (B, "git $cmd"), (P, "git $cmd"), (B, "git push $flags"), (B, "git push origin $ref"),
    (B, "git tag $name"), (B, "git commit $opts -m x"), (B, "gh pr merge 1 $opts"),
    (B, "gh api -X $m repos/o/r"), (B, "gh $cmd"), (B, "git -c $kv commit -m x"),
    # text that cannot be read in full
    (B, "git push 'unterminated"), (P, "git push \"unterminated"), (P, "git status; <# git push -f"),
    (B, "git status $(echo"),
]

# (tool, command) the guard must let through.
ALLOW = [
    (B, "git tag"), (P, "git tag"), (B, "git tag -l"), (P, "git tag -l"), (B, "git tag --list"),
    (P, "git tag --list 'v*'"), (B, "git tag -l 'Cli-v*' --sort=-version:refname"), (B, "git tag -n5"),
    (B, "git tag --contains HEAD"), (B, "git tag --points-at HEAD"), (B, "git tag --merged main"),
    (B, "git describe"), (P, "git describe --tags --abbrev=0"),
    (B, "gh api repos/o/r/pulls"), (P, "gh api repos/o/r/pulls --jq '.[].number'"),
    (B, "gh api -X GET repos/o/r"), (P, "gh api --method get repos/o/r --paginate"),
    (B, "gh api -H 'Accept: application/vnd.github+json' repos/o/r/issues/1"),
    (B, "gh pr merge 12 --squash --match-head-commit abc123"),
    (P, "gh pr merge 12 -s --match-head-commit=abc123 --delete-branch"),
    (B, "gh pr merge 12 --squash --match-head-commit abc123 --auto"),
    (B, "gh release list"), (P, "gh release view v1.0.0"), (B, "gh release download v1 -p '*.zip'"),
    (B, "gh workflow run test.yml --ref my-branch"), (P, "gh workflow run 'Python Tests'"),
    (B, "gh workflow list"), (B, "gh run view 123 --log-failed"), (B, "gh run list"),
    (B, "git status && git log --oneline -5"), (P, "git status; git log --oneline -5"),
    (B, "git push origin HEAD"), (P, "git push -u origin my-branch"), (B, "git push --set-upstream origin x"),
    (B, "git push --dry-run origin x"), (B, "git push -n origin x"), (B, "git push origin feature/v2-notes"),
    (B, "git commit -m 'fix: thing'"), (P, "git commit -m 'fix: thing'"),
    (B, "git commit -m \"$(cat <<'EOF'\nFix: don't break the parser\n\nCo-Authored-By: someone\nEOF\n)\""),
    (P, "git commit -m @'\nFix: don't break the parser\n'@"), (B, "git commit -F msg.txt"),
    (B, "git commit --amend --no-edit"), (B, "git commit -m 'a' -m 'b'"), (B, "git commit -mnot-a-flag"),
    (B, "git reset --soft HEAD~1"), (B, "git reset HEAD file.txt"), (B, "git config user.name"),
    (B, "git rebase main"), (B, "git merge --ff-only main"),
    (B, "dotnet build PPDS.sln -v q"), (P, "dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj"),
    (B, "dotnet pack src/PPDS.Cli -c Release"), (B, "dotnet nuget list source"),
    (B, "npm run ext:test"), (P, "npm install --prefix src/PPDS.Extension"), (B, "npm run vsce:package --prefix src/PPDS.Extension"),
    (B, "echo hi # it's a comment"), (P, "Write-Output 'hi' # it's a comment"),
    (P, "Get-ChildItem -Recurse .claude | Select-Object -First 5"), (B, "python -m pytest tests/ -q"),
    (P, "$env:FOO = 'bar'; python -m pytest tests/ -q"),
]


@pytest.mark.parametrize("tool,command", REFUSE, ids=[f"{t}:{c[:60]}" for t, c in REFUSE])
def test_refused(tool, command):
    reasons = guard.check_command(command, tool, None)
    assert reasons, f"{tool} command was not refused: {command!r}"


@pytest.mark.parametrize("tool,command", ALLOW, ids=[f"{t}:{c[:60]}" for t, c in ALLOW])
def test_allowed(tool, command):
    reasons = guard.check_command(command, tool, None)
    assert reasons == [], f"{tool} command was refused: {command!r}: {reasons}"


def test_refuses_a_local_tag_pushed_by_bare_name(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    git = ["git", "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@example.invalid"]
    for args in (["init", "-q"], ["commit", "-q", "--allow-empty", "-m", "x"], ["tag", "release-candidate"]):
        subprocess.run(git + args, check=True, stdin=subprocess.DEVNULL, capture_output=True)
    assert guard.check_command("git push origin release-candidate", "Bash", str(repo))
    assert guard.check_command("git push origin main", "Bash", str(repo)) == []


@pytest.mark.parametrize("tool", [B, P])
def test_hook_refuses_with_a_message_naming_the_maintainer(tool):
    r = _run(json.dumps(_payload(tool, "git push --force")))
    assert r.returncode == 2
    assert "BLOCKED [command-text-guard]: refused a force push (git push --force)." in r.stderr
    assert "the maintainer's actions" in r.stderr


@pytest.mark.parametrize("tool", [B, P])
def test_hook_allows(tool):
    r = _run(json.dumps(_payload(tool, "git tag -l")))
    assert r.returncode == 0, r.stderr
    assert r.stderr == ""


def test_unreadable_hook_input_is_refused():
    r = _run("not json{{")
    assert r.returncode == 2
    assert "hook input could not be read" in r.stderr


def test_shell_tool_without_a_command_is_refused():
    r = _run(json.dumps({"tool_name": "PowerShell", "tool_input": {}}))
    assert r.returncode == 2


def test_other_tools_pass():
    r = _run(json.dumps({"tool_name": "Read", "tool_input": {"file_path": "x"}}))
    assert r.returncode == 0


def test_a_long_command_is_read_to_the_end():
    command = "git status; " * 2000 + "git push --force"
    assert guard.check_command(command, "Bash", None)
    assert guard.check_command(command, "PowerShell", None)


def test_text_beyond_the_length_cap_is_refused_when_it_mentions_git():
    command = "echo " + "x" * (guard.sp.MAX_TEXT_CHARS + 10) + "; git status"
    assert guard.check_command(command, "Bash", None)
