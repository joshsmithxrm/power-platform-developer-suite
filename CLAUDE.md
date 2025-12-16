# CLAUDE.md - ppds-sdk

**NuGet packages for Power Platform plugin development.**

**Part of the PPDS Ecosystem** - See `C:\VS\ppds\CLAUDE.md` for cross-project context.

---

## 🚫 NEVER

| Rule | Why |
|------|-----|
| Regenerate `PPDS.Plugins.snk` | Breaks strong naming; existing assemblies won't load |
| Remove nullable reference types | Type safety prevents runtime errors |
| Skip XML documentation on public APIs | Consumers need IntelliSense documentation |
| Multi-target without testing all frameworks | Dataverse has specific .NET requirements |
| Commit with failing tests | All tests must pass before merge |

---

## ✅ ALWAYS

| Rule | Why |
|------|-----|
| Strong name all assemblies | Required for Dataverse plugin sandbox |
| XML documentation for public APIs | IntelliSense support for consumers |
| Multi-target 4.6.2, 6.0, 8.0 | Dataverse compatibility across versions |
| Run `dotnet test` before PR | Ensures no regressions |
| Update `CHANGELOG.md` with changes | Release notes for consumers |
| Follow SemVer versioning | Clear compatibility expectations |

---

## 💻 Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 6.0, 8.0 | Multi-targeting for Dataverse compatibility |
| C# | Latest (LangVersion) | Primary language |
| NuGet | - | Package distribution |
| Strong Naming | .snk file | Required for Dataverse plugin assemblies |

---

## 📁 Project Structure

```
ppds-sdk/
├── src/
│   └── PPDS.Plugins/
│       ├── Attributes/          # PluginStepAttribute, PluginImageAttribute
│       ├── Enums/               # PluginStage, PluginMode, PluginImageType
│       ├── PPDS.Plugins.csproj
│       └── PPDS.Plugins.snk     # Strong name key (DO NOT regenerate)
├── tests/
│   └── PPDS.Plugins.Tests/
├── .github/workflows/
│   ├── build.yml               # CI build
│   ├── test.yml                # CI tests
│   └── publish-nuget.yml       # Release → NuGet.org
├── PPDS.Sdk.sln
└── CHANGELOG.md
```

---

## 🛠️ Common Commands

```powershell
# Build
dotnet build                           # Debug build
dotnet build -c Release                # Release build

# Test
dotnet test                            # Run all tests
dotnet test --logger "console;verbosity=detailed"

# Pack (local testing)
dotnet pack -c Release -o ./nupkgs     # Create NuGet package

# Clean
dotnet clean
```

---

## 🔄 Development Workflow

### Making Changes

1. Create feature branch from `main`
2. Make changes
3. Run `dotnet build` and `dotnet test`
4. Update `CHANGELOG.md`
5. Create PR to `main`

### Code Conventions

```csharp
// ✅ Correct - Use nullable reference types
public string? OptionalProperty { get; set; }

// ❌ Wrong - Missing nullability
public string OptionalProperty { get; set; }
```

```csharp
// ✅ Correct - XML documentation on public API
/// <summary>
/// Defines a plugin step registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PluginStepAttribute : Attribute { }

// ❌ Wrong - No documentation
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PluginStepAttribute : Attribute { }
```

### Namespaces

```csharp
namespace PPDS.Plugins;              // Root
namespace PPDS.Plugins.Attributes;   // Attributes
namespace PPDS.Plugins.Enums;        // Enums
```

---

## 📦 Version Management

- Version is in `src/PPDS.Plugins/PPDS.Plugins.csproj`
- Follow SemVer: `MAJOR.MINOR.PATCH`
- Update version in `.csproj` before release
- Tag releases as `vX.Y.Z`

---

## 🔀 Git Branch & Merge Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Protected, always releasable |
| `feature/*` | New features |
| `fix/*` | Bug fixes |

**Merge Strategy:** Squash merge to main (clean commit history)

---

## 🚀 Release Process

1. Update version in `PPDS.Plugins.csproj`
2. Update `CHANGELOG.md`
3. Merge to `main`
4. Create GitHub Release with tag `vX.Y.Z`
5. `publish-nuget.yml` workflow automatically publishes to NuGet.org

**Required Secret:** `NUGET_API_KEY`

---

## 🔗 Ecosystem Integration

**This package is used by:**
- **ppds-demo** - Reference implementation
- **Customer plugin projects** - Via NuGet reference

**Extracted by:**
- **ppds-tools** - `Get-DataversePluginRegistrations` reads these attributes

---

## 📋 Key Files

| File | Purpose |
|------|---------|
| `PPDS.Plugins.csproj` | Project config, version, NuGet metadata |
| `PPDS.Plugins.snk` | Strong name key (DO NOT regenerate) |
| `CHANGELOG.md` | Release notes |
| `.editorconfig` | Code style settings |

---

## 🧪 Testing Requirements

- **Target 80% code coverage**
- Unit tests for all public API (attributes, enums)
- Run `dotnet test` before submitting PR
- All tests must pass before merge

---

## ⚖️ Decision Presentation

When presenting choices or asking questions:
1. **Lead with your recommendation** and rationale
2. **List alternatives considered** and why they're not preferred
3. **Ask for confirmation**, not open-ended input

❌ "What testing approach should we use?"
✅ "I recommend X because Y. Alternatives considered: A (rejected because B), C (rejected because D). Do you agree?"
