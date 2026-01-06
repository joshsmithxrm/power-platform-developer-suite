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
- Skip for non-user-facing changes (tests, docs, internal refactoring)

### 5. Test Coverage (New Code) — BLOCKING

Run the test coverage check script:
```powershell
.\scripts\Test-NewCodeCoverage.ps1
```

**If tests are missing, this is a blocker.** Do NOT proceed without tests.

**Action when tests are missing:**
1. Read each source file that needs tests
2. Create the test project if it doesn't exist (e.g., `PPDS.Auth.Tests`)
3. Write complete, meaningful unit tests — NOT stubs
4. Tests must have real assertions that verify behavior
5. Run the tests to confirm they pass

**What constitutes a real test:**
```csharp
// ❌ WRONG - This is a stub, provides no value
[Fact]
public void SomeMethod_ShouldWork()
{
    // TODO: Implement test
}

// ❌ WRONG - Tests existence, not behavior
[Fact]
public void Constructor_DoesNotThrow()
{
    var sut = new ProfileCollection();
    Assert.NotNull(sut);
}

// ✅ CORRECT - Tests actual behavior
[Fact]
public void Add_FirstProfile_SetsAsActive()
{
    var collection = new ProfileCollection();
    var profile = new AuthProfile { Name = "test" };

    collection.Add(profile);

    Assert.Equal(profile, collection.ActiveProfile);
}
```

**Only skip tests if:**
- File is genuinely untestable (tight external coupling)
- File is already covered by integration tests in PPDS.LiveTests
- User explicitly confirms "this doesn't need unit tests because [reason]"

### 6. CLI README Consistency

**Only applies when `src/PPDS.Cli/Commands/` files are modified.**

1. **Command structure matches reality**
   ```bash
   ppds data --help   # or whichever command group changed
   ```
   Compare subcommand list against README "Command Structure" section at top of file.

2. **New commands have README sections**
   - Check `src/PPDS.Cli/README.md` for corresponding `#### CommandName` section
   - Must have: examples, options list
   - Destructive commands (update, delete, clean) need "Safety Features" callout

**If README is outdated:** Update it and amend the commit before proceeding.

### 7. Base Branch Check & Push

Before creating PR, ensure the branch is based on latest `origin/main`:

```bash
# Fetch latest from origin
git fetch origin

# Check if current branch is behind origin/main
git rev-list --count HEAD..origin/main
```

**If the count is > 0, the branch is behind `origin/main`.** Rebase before pushing:

```bash
# Rebase onto latest main
git rebase origin/main

# If conflicts, resolve them and continue
git rebase --continue
```

**Only after rebasing (if needed)**, push the branch:

```bash
# Check status
git status

# Push (or force-push after rebase)
git push -u origin "$(git rev-parse --abbrev-ref HEAD)"
# If rebased, may need: git push --force-with-lease
```

**The PR cannot be created if commits aren't pushed.** This is a common oversight.

## Output

```
Pre-PR Validation
=================
[✓] Build: PASS
[✓] Tests: PASS (42 passed)
[!] Missing XML docs: MsalClientBuilder.CreateClient()
[✓] No TODOs found
[✗] Missing tests for: EnvironmentResolutionService, ProfileValidator
[✓] CLI README: Command structure matches (or N/A if no CLI changes)
[✓] Base branch: Up to date with origin/main (or "Behind by N commits - rebasing...")

Missing tests is a blocker. Writing tests now...
```

## Behavior

1. Run all checks
2. Report results
3. **If tests are missing: write them** (do not ask, do not generate stubs)
4. If unable to write tests, explain why and get user confirmation to skip
5. Run tests again after writing new ones
6. Only proceed to PR when all checks pass

## When to Use

- Before `git commit` for significant changes
- Before `gh pr create` (includes push verification)
- After addressing bot review comments
