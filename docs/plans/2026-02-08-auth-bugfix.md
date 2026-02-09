# Fix Interactive & Device Code Auth in TUI Profile Creation - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix the TUI profile creation flow so the user's explicit auth method selection is respected and all interactive callbacks (pre-auth dialog, device code display) work for both Browser and Device Code auth.

**Architecture:** The profile creation flow (TUI → `ProfileService`) bypasses `CredentialProviderFactory` entirely, using its own `CreateCredentialProvider` and `DetermineAuthMethod`. The fix threads the user's explicit `AuthMethod` selection and callbacks through this code path. Previous commit `983d916` fixed the factory/provider layer (ongoing connections); this plan fixes the profile creation layer.

**Tech Stack:** C# / .NET 8+, Terminal.Gui 1.19+, MSAL, xUnit, FluentAssertions, Moq

---

## Context for Engineers

### Code paths for creating credential providers

| Path | When | Factory used? | Pre-auth dialog? |
|------|------|---------------|-------------------|
| `InteractiveSession` → `ProfileServiceFactory` → `CredentialProviderFactory` | Ongoing connections from stored profiles | Yes | Yes (via `PpdsApplication.beforeInteractiveAuth`) |
| `ProfileCreationDialog` → `ProfileService.CreateProfileAsync` → `CreateCredentialProvider` | TUI profile creation | **No** | **No (bug)** |
| `AuthCommandGroup.ExecuteCreateAsync` | CLI `ppds auth create` | No (inline) | No (CLI, not needed) |

### What's broken in the profile creation path

1. `ProfileCreateRequest` has no `AuthMethod` field — TUI encodes selection as `UseDeviceCode` bool, service guesses via `DetermineAuthMethod()` fallback
2. `ProfileCreationDialog.OnAuthenticateClicked` only creates `deviceCodeCallback` when `method == DeviceCode` — Browser auth gets null callback
3. `ProfileService.CreateCredentialProvider` creates `InteractiveBrowserCredentialProvider` without `deviceCodeCallback` or `beforeInteractiveAuth` — no pre-auth dialog, no device code fallback
4. `IProfileService.CreateProfileAsync` doesn't accept `beforeInteractiveAuth` parameter

### Reference: How the working path wires callbacks

`PpdsApplication.cs:50-84` creates a `beforeInteractiveAuth` callback that:
- Marshals to UI thread via `Application.MainLoop.Invoke`
- Runs `PreAuthenticationDialog` (Open Browser / Use Device Code / Cancel)
- Uses `ManualResetEventSlim` for cross-thread synchronization
- Returns `PreAuthDialogResult` to the calling auth provider

Profile creation must replicate this pattern.

---

## Tasks

### Task 1: Write failing test — explicit AuthMethod in ProfileCreateRequest

**Files:**
- Test: `tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs`

**Step 1: Write the failing test**

`ProfileCreateRequest` must accept an explicit `AuthMethod?` property. This is a compilation test — the property doesn't exist yet.

```csharp
// Add to ProfileServiceTests.cs — new region at the end

#region ProfileCreateRequest AuthMethod Tests

[Fact]
public void ProfileCreateRequest_AuthMethod_CanBeSetExplicitly()
{
    var request = new ProfileCreateRequest
    {
        AuthMethod = AuthMethod.DeviceCode
    };

    Assert.Equal(AuthMethod.DeviceCode, request.AuthMethod);
}

[Fact]
public void ProfileCreateRequest_AuthMethod_DefaultsToNull()
{
    var request = new ProfileCreateRequest();

    Assert.Null(request.AuthMethod);
}

#endregion
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ProfileServiceTests.ProfileCreateRequest_AuthMethod" --framework net9.0`
Expected: BUILD FAIL — `ProfileCreateRequest` does not contain a definition for `AuthMethod`

**Step 3: Add `AuthMethod?` property to ProfileCreateRequest**

Modify: `src/PPDS.Cli/Services/Profile/IProfileService.cs:209` — add after `UseDeviceCode`:

```csharp
/// <summary>
/// Explicit auth method selection. When set, takes priority over UseDeviceCode and field-based inference.
/// Used by TUI to pass the user's radio button selection directly.
/// </summary>
public AuthMethod? AuthMethod { get; init; }
```

Add the using at top of file:
```csharp
using PPDS.Auth.Profiles;  // Already present — AuthMethod enum is in this namespace
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ProfileServiceTests.ProfileCreateRequest_AuthMethod" --framework net9.0`
Expected: PASS

**Step 5: Commit**

```
git add src/PPDS.Cli/Services/Profile/IProfileService.cs tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs
git commit -m "feat(auth): add explicit AuthMethod property to ProfileCreateRequest"
```

---

### Task 2: Write failing test — DetermineAuthMethod prefers explicit AuthMethod

`DetermineAuthMethod` is `private static` in `ProfileService`. Test indirectly: when `AuthMethod` is set on the request, the profile created by `CreateProfileAsync` should have that method. However, `CreateProfileAsync` triggers real auth which we can't do in unit tests.

Instead, test that `BuildCreateRequest` in the TUI sets the `AuthMethod` field. But that's a TUI test. Since `DetermineAuthMethod` is a pure function, the cleanest approach is to make it `internal` so tests can call it directly.

**Files:**
- Modify: `src/PPDS.Cli/Services/Profile/ProfileService.cs:391`
- Test: `tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs`

**Step 1: Write the failing test**

```csharp
// Add to ProfileServiceTests.cs

#region DetermineAuthMethod Tests

[Fact]
public void DetermineAuthMethod_WithExplicitAuthMethod_ReturnsExplicitValue()
{
    var request = new ProfileCreateRequest { AuthMethod = AuthMethod.InteractiveBrowser };

    var result = ProfileService.DetermineAuthMethod(request);

    Assert.Equal(AuthMethod.InteractiveBrowser, result);
}

[Fact]
public void DetermineAuthMethod_WithExplicitDeviceCode_ReturnsDeviceCode()
{
    var request = new ProfileCreateRequest { AuthMethod = AuthMethod.DeviceCode };

    var result = ProfileService.DetermineAuthMethod(request);

    Assert.Equal(AuthMethod.DeviceCode, result);
}

[Fact]
public void DetermineAuthMethod_WithUseDeviceCodeFlag_ReturnsDeviceCode()
{
    // Backward compat: CLI-style request without explicit AuthMethod
    var request = new ProfileCreateRequest { UseDeviceCode = true };

    var result = ProfileService.DetermineAuthMethod(request);

    Assert.Equal(AuthMethod.DeviceCode, result);
}

[Fact]
public void DetermineAuthMethod_WithClientSecret_ReturnsClientSecret()
{
    var request = new ProfileCreateRequest { ClientSecret = "secret" };

    var result = ProfileService.DetermineAuthMethod(request);

    Assert.Equal(AuthMethod.ClientSecret, result);
}

[Fact]
public void DetermineAuthMethod_ExplicitAuthMethod_TakesPriorityOverFlags()
{
    // Explicit AuthMethod should win even if UseDeviceCode is also set
    var request = new ProfileCreateRequest
    {
        AuthMethod = AuthMethod.InteractiveBrowser,
        UseDeviceCode = true
    };

    var result = ProfileService.DetermineAuthMethod(request);

    Assert.Equal(AuthMethod.InteractiveBrowser, result);
}

#endregion
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ProfileServiceTests.DetermineAuthMethod" --framework net9.0`
Expected: BUILD FAIL — `ProfileService.DetermineAuthMethod` is inaccessible due to its protection level

**Step 3: Change DetermineAuthMethod from private to internal, add early return**

Modify: `src/PPDS.Cli/Services/Profile/ProfileService.cs:391`

Change:
```csharp
private static AuthMethod DetermineAuthMethod(ProfileCreateRequest request)
{
```
To:
```csharp
internal static AuthMethod DetermineAuthMethod(ProfileCreateRequest request)
{
    if (request.AuthMethod.HasValue)
        return request.AuthMethod.Value;
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ProfileServiceTests.DetermineAuthMethod" --framework net9.0`
Expected: PASS — all 5 tests pass

**Step 5: Commit**

```
git add src/PPDS.Cli/Services/Profile/ProfileService.cs tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs
git commit -m "feat(auth): DetermineAuthMethod prefers explicit AuthMethod over inference"
```

---

### Task 3: Add beforeInteractiveAuth parameter to CreateProfileAsync

**Files:**
- Modify: `src/PPDS.Cli/Services/Profile/IProfileService.cs:48-51`
- Modify: `src/PPDS.Cli/Services/Profile/ProfileService.cs:217-220`

**Step 1: Update the interface**

Change `IProfileService.CreateProfileAsync` signature:

```csharp
Task<ProfileSummary> CreateProfileAsync(
    ProfileCreateRequest request,
    Action<DeviceCodeInfo>? deviceCodeCallback = null,
    Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
    CancellationToken cancellationToken = default);
```

**Step 2: Update the implementation signature**

Change `ProfileService.CreateProfileAsync`:

```csharp
public async Task<ProfileSummary> CreateProfileAsync(
    ProfileCreateRequest request,
    Action<DeviceCodeInfo>? deviceCodeCallback = null,
    Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
    CancellationToken cancellationToken = default)
```

**Step 3: Pass beforeInteractiveAuth to CreateCredentialProvider**

Change the call at `ProfileService.cs:320`:

```csharp
using ICredentialProvider provider = CreateCredentialProvider(request, authMethod, cloud, deviceCodeCallback, beforeInteractiveAuth);
```

**Step 4: Update CreateCredentialProvider to accept and pass callbacks**

Change `ProfileService.cs:481-506`:

```csharp
private static ICredentialProvider CreateCredentialProvider(
    ProfileCreateRequest request,
    AuthMethod authMethod,
    CloudEnvironment cloud,
    Action<DeviceCodeInfo>? deviceCodeCallback,
    Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth)
{
    return authMethod switch
    {
        AuthMethod.InteractiveBrowser => new InteractiveBrowserCredentialProvider(
            cloud, request.TenantId, deviceCodeCallback: deviceCodeCallback, beforeInteractiveAuth: beforeInteractiveAuth),
        AuthMethod.DeviceCode => new DeviceCodeCredentialProvider(cloud, request.TenantId, deviceCodeCallback: deviceCodeCallback),
        // ... all other cases unchanged
    };
}
```

**Step 5: Verify build succeeds**

Run: `dotnet build src/PPDS.Cli --framework net9.0`
Expected: Build succeeded

**Step 6: Run existing tests to verify no regressions**

Run: `dotnet test tests/PPDS.Cli.Tests --framework net9.0 -v q`
Expected: All tests pass

**Step 7: Commit**

```
git add src/PPDS.Cli/Services/Profile/IProfileService.cs src/PPDS.Cli/Services/Profile/ProfileService.cs
git commit -m "feat(auth): pass beforeInteractiveAuth through profile creation flow"
```

---

### Task 4: Fix ProfileCreationDialog — always create callbacks, pass AuthMethod

**Files:**
- Modify: `src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs`

**Step 1: Update BuildCreateRequest to pass explicit AuthMethod**

At `ProfileCreationDialog.cs:580-600`, add `AuthMethod = method`:

```csharp
private ProfileCreateRequest BuildCreateRequest()
{
    var method = GetSelectedMethod();

    return new ProfileCreateRequest
    {
        Name = string.IsNullOrWhiteSpace(_nameField.Text?.ToString()) ? null : _nameField.Text.ToString()?.Trim(),
        Environment = string.IsNullOrWhiteSpace(_environmentUrlField.Text?.ToString()) ? null : _environmentUrlField.Text.ToString()?.Trim(),
        AuthMethod = method,
        UseDeviceCode = method == AuthMethod.DeviceCode,
        // SPN fields
        ApplicationId = _appIdField.Text?.ToString()?.Trim(),
        TenantId = _tenantIdField.Text?.ToString()?.Trim(),
        ClientSecret = method == AuthMethod.ClientSecret ? _clientSecretField.Text?.ToString() : null,
        CertificatePath = method == AuthMethod.CertificateFile ? _certPathField.Text?.ToString()?.Trim() : null,
        CertificatePassword = method == AuthMethod.CertificateFile ? _certPasswordField.Text?.ToString() : null,
        CertificateThumbprint = method == AuthMethod.CertificateStore ? _thumbprintField.Text?.ToString()?.Trim() : null,
        // Username/Password fields
        Username = method == AuthMethod.UsernamePassword ? _usernameField.Text?.ToString()?.Trim() : null,
        Password = method == AuthMethod.UsernamePassword ? _passwordField.Text?.ToString() : null,
    };
}
```

**Step 2: Always create deviceCodeCallback, create beforeInteractiveAuth for Browser**

Replace the callback creation block in `OnAuthenticateClicked` (`ProfileCreationDialog.cs:493-518`):

```csharp
// Always provide device code callback (needed for Browser's device code fallback too)
var deviceCallback = _deviceCodeCallback ?? (info =>
{
    Application.MainLoop?.Invoke(() =>
    {
        // Auto-copy code to clipboard for convenience
        var copied = ClipboardHelper.CopyToClipboard(info.UserCode) ? " (copied!)" : "";

        // MessageBox is safe from MainLoop.Invoke - doesn't start nested event loop
        MessageBox.Query(
            "Authentication Required",
            $"Visit: {info.VerificationUrl}\n\n" +
            $"Enter code: {info.UserCode}{copied}\n\n" +
            "Complete authentication in browser, then press OK.",
            "OK");
    });
});

// Pre-auth dialog for Browser auth (matches PpdsApplication.cs pattern)
Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeAuth = null;
if (method == AuthMethod.InteractiveBrowser)
{
    beforeAuth = (dcCallback) =>
    {
        var result = PreAuthDialogResult.Cancel;
        using var waitHandle = new ManualResetEventSlim(false);
        Application.MainLoop?.Invoke(() =>
        {
            try
            {
                Application.Refresh();
                var dialog = new PreAuthenticationDialog(dcCallback);
                Application.Run(dialog);
                result = dialog.Result;
            }
            finally
            {
                waitHandle.Set();
            }
        });
        waitHandle.Wait();
        return result;
    };
}

_statusLabel.Text = "Authenticating...";
Application.Refresh();

_errorService?.FireAndForget(CreateProfileAndHandleResultAsync(request, deviceCallback, beforeAuth), "CreateProfile");
```

**Step 3: Update CreateProfileAndHandleResultAsync signature and call**

Change `ProfileCreationDialog.cs:521`:

```csharp
private async Task CreateProfileAndHandleResultAsync(
    ProfileCreateRequest request,
    Action<DeviceCodeInfo>? deviceCodeCallback,
    Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth)
{
    try
    {
        var profile = await _profileService.CreateProfileAsync(request, deviceCodeCallback, beforeInteractiveAuth, _cts.Token);
```

**Step 4: Verify build succeeds**

Run: `dotnet build src/PPDS.Cli --framework net9.0`
Expected: Build succeeded

**Step 5: Run all tests**

Run: `dotnet test --filter "Category!=Integration" --framework net9.0 -v q`
Expected: All tests pass

**Step 6: Commit**

```
git add src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs
git commit -m "fix(auth): wire callbacks and explicit AuthMethod through TUI profile creation

ProfileCreationDialog now:
- Passes explicit AuthMethod in ProfileCreateRequest
- Always creates deviceCodeCallback (not just for DeviceCode)
- Creates beforeInteractiveAuth for Browser auth showing PreAuthenticationDialog
- Passes both callbacks through to ProfileService.CreateProfileAsync

This enables the pre-auth dialog (Open Browser / Use Device Code / Cancel)
during profile creation, matching the behavior of ongoing connections."
```

---

### Task 5: Write test for ProfileCreationDialog.BuildCreateRequest setting AuthMethod

The TUI dialog requires `Application.Init()` for complex views. Use the `CaptureState()` pattern established in the codebase — but `BuildCreateRequest` is private. Test indirectly via the `CaptureState` output or by verifying the `AuthMethod` field exists on the request type (already covered in Task 1). The functional integration is covered by manual testing.

**Skip** — sufficient coverage from Task 1 (request accepts AuthMethod) and Task 2 (DetermineAuthMethod prefers it). The wiring in Task 4 is a mechanical pass-through.

---

### Task 6: Full regression test suite

**Step 1: Run all unit tests**

Run: `dotnet test --filter "Category!=Integration" --framework net9.0 -v q`
Expected: All tests pass, 0 failures

**Step 2: Commit plan update**

```
git add docs/plans/2026-02-08-auth-bugfix.md
git commit -m "docs: update auth bugfix plan with TUI profile creation fixes"
```
