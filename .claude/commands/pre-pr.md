# Pre-PR Validation

Run before creating a PR to catch issues early.

## Usage

`/pre-pr`

## Checks Performed

### 1. Build & Test
```bash
dotnet build -c Release --warnaserror
dotnet test --no-build -c Release
```

### 2. New Public APIs
- Check for public classes/methods without XML documentation
- Check for new public APIs without corresponding tests

### 3. Common Issues
- Generic catch clauses (`catch { }` or `catch (Exception)` without logging)
- TODO/FIXME comments that should be issues
- Console.WriteLine in library code (should use ILogger or AuthenticationOutput)
- Dead code (unused private methods, unreachable code)

### 4. Changelog
- Verify CHANGELOG.md in affected package(s) is updated
- If not, prompt: "Update changelog for [package]?"

### 5. Test Coverage (New Code)
- For each new class: does corresponding test file exist?
- Prompt: "No tests for [ClassName]. Add tests?" → Generate test stubs

## Output

```
Pre-PR Validation
=================
[x] Build: PASS
[x] Tests: PASS (42 passed)
[!] Missing XML docs: MsalClientBuilder.CreateClient()
[x] No TODOs found
[!] Changelog not updated for PPDS.Auth
[!] No tests for: ThrottleDetector, MsalClientBuilder

Fix issues? [Y/n]
```

## Behavior

- On first failure: stop and report
- On warnings: list all, ask whether to fix
- Auto-fix what's possible (changelog stubs, test file creation)
- Manual fix guidance for others

## When to Use

- Before `git commit` for significant changes
- Before `gh pr create`
- After addressing bot review comments
