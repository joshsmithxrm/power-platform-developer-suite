# Release Coordination

Playbooks for coordinated releases across PPDS repos.

---

## Single-Repo Release (non-breaking)

1. Update version in manifest/csproj
2. Update CHANGELOG.md
3. Verify CI passes
4. Create PR, merge to main
5. Create GitHub Release with tag
6. Verify package published

---

## Cross-Repo Breaking Release

### Checklist

- [ ] Identify all affected repos
- [ ] Plan release order (ppds → ppds-tools → ppds-alm)
- [ ] Update compatibility matrix in VERSIONING_POLICY.md
- [ ] For each repo:
  - [ ] Update minimum version requirements
  - [ ] Update CHANGELOG with breaking change notes
  - [ ] Coordinate release timing
- [ ] Update ppds-demo to use new versions

---

## Scenario: PPDS Breaking Change

Example: `PluginStepAttribute` adds required property

1. **ppds** (monorepo)
   - Bump to 2.0.0
   - Update CHANGELOG with migration guide
   - Release to NuGet

2. **ppds-tools**
   - Update reflection code for new attribute
   - Bump to 2.0.0
   - Document: "Requires assemblies built with PPDS.Plugins 2.0+"
   - Release to PSGallery

3. **ppds-alm**
   - Update `Install-Module` to `-MinimumVersion '2.0.0'`
   - Bump to 2.0.0
   - Tag and update `v2` alias

4. **ppds-demo**
   - Update NuGet reference to PPDS.Plugins 2.0.0
   - Update workflow refs to ALM `@v2`

---

## Scenario: ppds-tools New Feature (non-breaking)

Example: `Deploy-DataversePlugins` adds optional `-Parallel` switch

1. **ppds-tools**
   - Bump to 1.3.0
   - Release to PSGallery

2. **ppds-alm** (if using the feature)
   - Add workflow input for parallel option
   - Bump to 1.2.0
   - Update minimum ppds-tools version to 1.3.0

3. **ppds/ppds-demo**
   - No changes needed

---

## Scenario: ppds-alm-Only Change

Example: New workflow for solution validation

1. **ppds-alm**
   - Add new workflow
   - Bump minor version
   - Release

2. **Other repos**
   - No changes needed

---

## Pre-Release Coordination

### Testing a ppds-tools pre-release in ppds-alm

```yaml
# On ppds-alm feature branch, temporarily:
- name: Install PPDS.Tools (prerelease)
  run: |
    Install-Module PPDS.Tools -AllowPrerelease -Force
```

### Testing a ppds pre-release in ppds-tools

```powershell
# In ppds-tools test environment:
dotnet tool install --global ppds --prerelease
```

---

## Version Status Check

```powershell
# Check all repo versions from your PPDS base directory
# Run this from your ppds parent folder (e.g., C:\Dev where ppds, ppds-docs, etc. live)
Get-ChildItem -Directory | Where-Object { Test-Path "$($_.FullName)\.git" } | ForEach-Object {
    $name = $_.Name
    $tag = git -C $_.FullName describe --tags --abbrev=0 2>$null
    [PSCustomObject]@{
        Repo = $name
        LatestTag = if ($tag) { $tag } else { "(none)" }
    }
} | Format-Table -AutoSize
```
