@AGENTS.md

## Claude Code notes

- The `.claude/` skills and hooks in this repo are optional. CI is the gate; see CONTRIBUTING.md "AI-assisted contributions".
- The Bash tool runs Git Bash, not PowerShell: do not call PowerShell cmdlets (`Test-Path`, `Get-ChildItem` and so on) or embed backslash paths in inline `python -c` strings; see `.claude/WORKFLOW.md` "Bash Tool Portability".
