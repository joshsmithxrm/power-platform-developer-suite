# ⚠️ TEMPORARY FILE - DELETE AFTER EXTENSION MIGRATION ⚠️

This file is a reminder for the extension migration session.

## Task: Pull Extension Code from Old Repo

The old `power-platform-developer-suite` repo (extension-only) needs to be migrated into `extension/` in this monorepo.

### Steps

1. Clone the old extension repo:
   ```bash
   git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git ../old-extension-temp
   ```

2. Review and copy relevant files:
   - Source files → `extension/src/`
   - Config files (.vscodeignore, .eslintrc, etc.) → `extension/`
   - Update package.json dependencies (marketplace identifiers already set)

3. Review old CI workflows and migrate as needed

4. Delete this temp clone:
   ```bash
   rm -rf ../old-extension-temp
   ```

5. **DELETE THIS FILE** after migration is complete

## Related
- Issue #282 - Repo consolidation
- Issue #290 - Extension v1 scope
- Session prompt saved for this work

---

**Created:** 2026-01-07
**Delete after:** Extension code successfully migrated to extension/
