# HyperPlot: agent entry

A NativeAOT-compatible plotting library in C# / .NET 10, built to draw billions of points at interactive speed: memory-mapped chunked columns, min/max LOD pyramids and M4 aggregation for series, density aggregation and spatial tiles for scatter, a Grammar-of-Graphics spec model, and Direct3D 11 compute and drawing through TerraFX.Interop.Windows. Hosts come in order: WPF, XylemUI, then Avalonia. This file is the entry point for agents. Steps for people are in `CONTRIBUTING.md`. Enforced rules are in `.cursor/rules/`.

## Source

| What | Where |
| --- | --- |
| Requirements (canonical, Japanese) | `docs/requirements.ja.md` |
| Design (canonical, Japanese) | `docs/design.ja.md` |
| Rejected alternatives | `docs/design-decisions.md` |
| Decisions with a measured reason | `docs/adr/` (one per file) |
| Order and status of work | `docs/roadmap.md` |
| Upstream code to read before a layer | `docs/references.md` |
| Projects, folders, coding conventions | `docs/conventions.md` |
| Build, format, and test gate | `./build.ps1` |

The current row lives only in `docs/roadmap.md`. `.dev/` is local scratch; nothing there is a decision.

## Required rules

Read the rules that match the work before changing anything. `alwaysApply` rules apply to every change; always read `workflow`, `consent`, `git`, and `scope`. After every merge comes the retrospective of `workflow`: what the work taught goes into these files, not into an agent's memory.

| Rule | One line |
| --- | --- |
| `workflow` | Grill, Issue, branch, PR, human squash merge, retrospective; labels; gates |
| `consent` | A question is not a decision; options need a recommendation; outward actions need an order |
| `git` | Finished work goes to a PR on its branch without asking; destructive git needs a named order; never `main` |
| `scope` | Requirements decide scope; no half-shipped capability |
| `defer` | Deferral only as a named roadmap row |
| `pace` | Split off what is outside the row; limit an investigation; long runs on the side |
| `open-ends` | No untimed frame-path stage and no unexplained figure in a PR |
| `record-corrections` | A correction lands in the rule, design, ADR, or skill it belongs to |
| `review-comments` | A finding is about the head it was written on; a review the owner asks for is posted on the PR |
| `similar-findings` | Fix the same kind of problem where it also occurs |
| `no-dead-code` | Remove everything that only served a removed path |
| `spec-checks` | A test implements the whole design statement |
| `scratch` | Remove scratch files in the turn that made them |
| `conventional-commits` | `type(scope): subject`, English; no `perf` type |
| `pull-requests` | Title and six-heading body from the template |
| `github-closing` | `Closes #N` alone on a line; never a closing verb before an unfinished Issue |
| `language` | Code and rules in English; the requirements, the design, Issues, and PR bodies may be Japanese |
| `design-docs` | Where the design lives; design PR before code; judge the current optimum |
| `powershell-shell` | Shell syntax on this setup |
| `engineering` | Layout, net10.0, warnings as errors, CSharpier, analyzers, banned APIs, the gate |
| `dependencies` | A dependency must keep performance control, NativeAOT, and no extra file to ship |
| `perf` | Zero allocation on the frame path; upload bounded by pixels, not data; dispatch; fast and slow paths; memory layout |
| `bench` | How time and allocations are measured; what a claim needs |
| `aot` | No reflection or dynamic code; the WPF exception; the NativeAOT publish check |
| `interop` | P/Invoke, COM, unsafe code, mapped memory, native callbacks (`src/**`) |
| `csharp` | Style, naming, types, errors, XML docs (`*.cs`) |
| `testing` | xUnit v3, fake time, test layers, test docs (`tests/**`) |
| `dev-docs` | Mermaid, math, and pseudo-code in `docs/` |

## Skills

Skills live in `.agents/skills/`. `.claude/skills/` holds a plain copy of the same folders, not links, so that git tracks both the same way on every OS. A change to a skill changes both copies in the same commit: a change to one copy alone leaves the other stale, and the next checkout shows it as changed. `skills-lock.json` pins the ones that come from outside.

- `grilling`: when the design branches, before an Issue.
- `github-closing`: before `gh pr create`, to check that the PR closes only what it finishes.
- `english-writing`: before writing XML docs, comments, test remarks, or rules.
- `cpu-profile`: where the time and allocations of a frame or a data pass go (NativeAOT or JIT).
- `natural-japanese`: write and check Japanese documents.

## Environment

- .NET 10 SDK (`global.json`). If `dotnet --list-sdks` shows none, install it; do not stop to ask.
- Windows is the primary host: the GPU tests (WARP), the WPF adapter, and the merge gate run there. Linux runs the layers that need neither a GPU nor WPF.
- The agent shell is PowerShell (`.cursor/rules/powershell-shell.mdc`).
