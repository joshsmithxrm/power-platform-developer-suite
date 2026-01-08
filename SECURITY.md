# Security Policy

## Supported Versions

We release security updates for the following versions:

| Package | Version | Supported |
|---------|---------|-----------|
| PPDS.Plugins | 1.x | ✅ |
| PPDS.Dataverse | 1.x | ✅ |
| PPDS.Migration | 1.x | ✅ |
| PPDS.Auth | 1.x | ✅ |
| PPDS.Cli | 1.x | ✅ |

**Recommendation:** Always use the latest version for best security and features.

---

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a security issue, please follow these steps:

### 1. Do NOT Open a Public Issue

Security vulnerabilities should **not** be reported via public GitHub issues. Public disclosure could put users at risk before a fix is available.

### 2. Report Privately

**Use GitHub's private vulnerability reporting:**
1. Go to the [Security tab](https://github.com/joshsmithxrm/power-platform-developer-suite/security)
2. Click "Report a vulnerability"
3. Fill out the form with details

### 3. Include in Your Report

- **Description**: Clear description of the vulnerability
- **Impact**: What an attacker could do (credential exposure, data access, etc.)
- **Steps to Reproduce**: Detailed steps or proof-of-concept code
- **Affected Packages**: Which PPDS packages are vulnerable
- **Suggested Fix**: If you have ideas (optional)

### 4. Response Timeline

- **Acknowledgment**: Within 48 hours
- **Initial Assessment**: Within 5 business days
- **Fix Development**: Depends on severity (critical: 7-14 days, high: 14-30 days)
- **Security Release**: Coordinated disclosure after fix is ready

---

## Security Practices

### What We Do

1. **Dependency Scanning**: Automated checks for vulnerable dependencies (GitHub Dependabot)
2. **Code Reviews**: All PRs reviewed for security issues
3. **Strong Naming**: All plugin assemblies are strong-named (required for Dataverse sandbox)
4. **Credential Protection**: Connection strings redacted from logs and error messages
5. **Static Analysis**: CodeQL and bot-based security review on PRs

### Credential Handling (PPDS.Auth)

**Authentication profiles** are stored securely:
- **Profile metadata** (environment URLs, tenant IDs): Stored in user-scoped config file
- **Tokens**: Cached using MSAL token cache (platform-encrypted)
- **Client secrets**: Never persisted; provided at runtime via environment variables or command line

**Best practices for users:**
- Use OIDC federation (GitHub Actions, Azure DevOps) instead of client secrets when possible
- Never commit credentials to source control
- Use environment variables for secrets in CI/CD
- Rotate client secrets regularly (90-day maximum recommended)

### Connection String Security (PPDS.Dataverse, PPDS.Migration)

- Connection strings are **never logged**, even at verbose level
- Error messages redact sensitive portions of connection info
- No PII is written to logs during migration operations

### CLI Input Validation (PPDS.Cli)

- All user inputs are validated before use
- File paths are validated to prevent path traversal
- URLs are validated for proper format

---

## Known Security Considerations

### 1. Local Token Cache

MSAL caches tokens locally for convenience. The cache location varies by platform:
- **Windows**: `%LOCALAPPDATA%\.ppds\`
- **macOS/Linux**: `~/.ppds/`

**Mitigation:** Cache uses platform encryption where available. For shared machines, use `ppds auth delete` to remove profiles after use.

### 2. CLI History

Commands with `--clientSecret` appear in shell history.

**Mitigation:** Use environment variables instead:
```bash
export PPDS_CLIENT_SECRET="your-secret"
ppds auth create --name ci --applicationId $APP_ID --tenant $TENANT_ID
```

### 3. NuGet Package Integrity

Packages are signed but not with a code signing certificate.

**Mitigation:** Verify package hashes against NuGet.org published values for sensitive deployments.

---

## Security Updates

### How to Stay Informed

1. **Watch this repository** (Releases only or All Activity)
2. **Check per-package CHANGELOG.md** for security fixes (marked with "Security")
3. **Subscribe to GitHub Security Advisories** for this repo

### Applying Security Updates

```bash
# Update CLI tool
dotnet tool update -g PPDS.Cli

# Update NuGet packages
dotnet add package PPDS.Dataverse  # Gets latest version
dotnet add package PPDS.Migration
dotnet add package PPDS.Auth
```

---

## Security Checklist for Contributors

If you're contributing code, please review:

- [ ] No hardcoded credentials or secrets
- [ ] User input validated and sanitized
- [ ] Connection strings not logged (use redaction helpers)
- [ ] Error messages don't leak sensitive information
- [ ] No logging of tokens, secrets, or credentials
- [ ] Dependencies up-to-date (`dotnet list package --outdated`)
- [ ] XML documentation doesn't expose internal security details

---

## Disclosure Policy

### Coordinated Disclosure

We follow **coordinated disclosure**:
1. Reporter notifies us privately
2. We develop and test a fix
3. We release patched packages
4. We publish a security advisory
5. Reporter can publish details (after patch release)

**Typical timeline:** 30-90 days from report to public disclosure

---

## Contact

- **Security Issues**: [GitHub Private Vulnerability Reporting](https://github.com/joshsmithxrm/power-platform-developer-suite/security/advisories/new)
- **General Security Questions**: Open a [GitHub Discussion](https://github.com/joshsmithxrm/power-platform-developer-suite/discussions)
- **Non-Security Bugs**: Open a [GitHub Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)

---

**Thank you for helping keep PPDS secure!**
