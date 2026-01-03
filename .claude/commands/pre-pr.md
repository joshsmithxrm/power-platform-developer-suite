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

## Output

```
Pre-PR Validation
=================
[✓] Build: PASS
[✓] Tests: PASS (42 passed)
[!] Missing XML docs: MsalClientBuilder.CreateClient()
[✓] No TODOs found
[✗] Missing tests for: EnvironmentResolutionService, ProfileValidator

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
- Before `gh pr create`
- After addressing bot review comments
