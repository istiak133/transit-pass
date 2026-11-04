# 0004. Build and CI conventions

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
Quality gates only work if they are automatic and cannot be skipped.

## Decision
- **Warnings are errors** solution-wide (`Directory.Build.props`), with the `latest-recommended` analyzer set and
  code-style enforced during build.
- **Central package management** (`Directory.Packages.props`): one version per package across the solution.
- **NuGet lock files** with `--locked-mode` in CI: a dependency graph that changed without a committed lock-file
  update fails the build, so a transitive dependency cannot change silently.
- **SDK pinned** in `global.json` (`latestFeature` roll-forward picks up patch and feature-band fixes, never a new major).
- **CI on every PR**: locked restore → `dotnet format --verify-no-changes` → Release build → tests with coverage.
  CodeQL `security-and-quality` runs on every PR and weekly.
- **`main` is protected**: changes land only via PR with green checks, squash-merged. The commit history reads as one
  commit per feature.
- **Dependabot** opens grouped weekly updates for NuGet packages and GitHub Actions.

## Consequences
- Adding a package means editing `Directory.Packages.props` and committing the regenerated lock files.
- Formatting disputes are settled by `.editorconfig`, not review comments.
