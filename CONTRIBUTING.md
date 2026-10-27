# Contributing to HyperPlot

HyperPlot is in its design phase. The requirements are `docs/requirements.ja.md` and the design is `docs/design.ja.md` (both Japanese), the order of work is `docs/roadmap.md`, and the conventions are `docs/conventions.md`.

## Workflow

1. If the design still branches, it is settled first (a Grill, then a `design` Issue that updates `docs/design.ja.md`).
2. Every change starts from an Issue opened with a template in `.github/ISSUE_TEMPLATE/`. Blank issues are disabled.
3. Branch from `main` as `type/<issue-number>-<slug>`.
4. Run `./build.ps1` before opening a pull request. It checks formatting (CSharpier), builds with warnings as errors, and runs every test project.
5. Open the pull request with the template. Put `Closes #N` alone on a line under `## Related`.
6. A maintainer reviews and squash-merges.

## Commits and pull requests

Titles follow Conventional Commits: `type(scope): subject`, in English. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `build`, `ci`.

A change on the frame path or the data path reports its time, its allocations, and its GPU upload before and after under `## Measurements`.

## Requirements

- .NET 10 SDK (`global.json`).
- Windows for the GPU tests (WARP), the WPF adapter, and the merge gate. Linux runs the other test layers.
- `dotnet tool restore` once after cloning (CSharpier).

## Versioning

Packages are versioned with SemVer from `VersionPrefix` in `Directory.Build.props`. Before 1.0, a minor version may break the public API. Nothing is published to NuGet yet.
