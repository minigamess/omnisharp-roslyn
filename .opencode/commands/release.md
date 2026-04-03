---
description: Bump version, push master, and create GitHub release
---
Run a release workflow with GitHub CLI only.

Requirements:
- Only run on `master`.
- Version argument is optional.
- If `$1` is provided, it must be SemVer (example: `/release 1.0.6`).
- If `$1` is not provided, auto-increment patch version from `<Version>` in `build/Settings.props`.
- Use `gh` for GitHub release creation.

Steps:
1. Check current branch with `git branch --show-current`; if not `master`, stop and explain.
2. Check working tree with `git status --porcelain`; if not clean, stop and list changed files.
3. Resolve target version:
   - Read current `<Version>` from `build/Settings.props`.
   - If `$1` exists, use `$1`.
   - If `$1` is empty, bump patch version automatically (e.g., `1.0.6` -> `1.0.7`).
4. Update `<Version>` in `build/Settings.props` to the resolved version.
5. Commit only `build/Settings.props` with message `chore(release): v<resolved_version>`.
6. Push with `git push origin master`.
7. Create release with GitHub CLI:
   - `gh release create v<resolved_version> --target master --title "v<resolved_version>" --generate-notes`
8. Verify release exists:
   - `gh release view v<resolved_version>`
9. Return a concise summary including commit hash and release URL.
