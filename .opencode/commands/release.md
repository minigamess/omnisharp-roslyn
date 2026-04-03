---
description: Bump version, push master, and create GitHub release
---
Run a release workflow with GitHub CLI only.

Requirements:
- Only run on `master`.
- Require a version argument in SemVer format as `$1` (example: `/release 1.0.6`).
- Use `gh` for GitHub release creation.

Steps:
1. Check current branch with `git branch --show-current`; if not `master`, stop and explain.
2. Check working tree with `git status --porcelain`; if not clean, stop and list changed files.
3. Update `<Version>` in `build/Settings.props` to `$1`.
4. Commit only `build/Settings.props` with message `chore(release): v$1`.
5. Push with `git push origin master`.
6. Create release with GitHub CLI:
   - `gh release create v$1 --target master --title "v$1" --generate-notes`
7. Verify release exists:
   - `gh release view v$1`
8. Return a concise summary including commit hash and release URL.
