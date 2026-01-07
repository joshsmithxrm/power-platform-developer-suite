# Run TUI tests

Run TUI unit and integration tests to validate TUI changes.

## Steps

1. Build the solution first:
   ```bash
   dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore
   ```

2. Run TuiUnit tests (fast, no external dependencies):
   ```bash
   dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --no-build
   ```

3. If TuiUnit tests pass, run TuiIntegration tests:
   ```bash
   dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiIntegration" --no-build
   ```

4. Report results:
   - If all pass: "TUI tests passed"
   - If failures: Analyze errors and suggest fixes

## Reference

- ADR-0028: TUI Testing Strategy
- `.claude/rules/TUI_TESTING.md`
