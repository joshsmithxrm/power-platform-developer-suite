# Task: Improve `publish --type entity` error messaging when used with `--solution`

## Branch Context
This is `pr/publish-env-lock-guidance` — improves publish command error UX by surfacing environment-lock faults with actionable guidance.

## Bug Report (from field testing)

### Issue 2: `ppds publish -t entity -s <solution>` error message is misleading

**Reproduction:**
```
ppds publish -t entity -s PawsClawsLabs -p PicassoLab -q -f Json
```

**Actual result:**
```
Specify entity logical names to publish.
Example: ppds publish --type entity account contact
```

**Problem:** The command accepts `--solution` and `--type entity` together without complaint, then fails at runtime saying entity names are required. The user reasonably expects that providing a solution would scope the publish to entities in that solution.

**Expected behavior (pick one, explain in commit message):**
1. Support solution-scoped entity publish (resolve entity names from solution membership), OR
2. Validate up-front: if `--type entity` is specified without explicit entity names, reject immediately with a clear message explaining that `--solution` alone is insufficient for entity publish, OR
3. At minimum, improve the error message to say: "Entity publish requires explicit logical names. --solution alone is not sufficient. Use: ppds publish --type entity account contact"

**Your task:**
1. Find the publish command implementation (likely `src/PlatformTools.Cli/Commands/Publish/`).
2. Trace how `--type entity` + `--solution` flows through validation.
3. Implement option 2 or 3 (option 1 is a feature addition — only do it if straightforward).
4. If you implement option 2, add the validation before any network call.
5. Run `dotnet test --filter "Category!=Integration" -v q` to verify no regressions.
