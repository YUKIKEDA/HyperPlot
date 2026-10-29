## Summary

<!-- What changed and why. Keep it short. May be Japanese. -->

-

## Related

<!-- `Closes #N` alone on a line when this PR finishes the Issue; `Part of #N` when it does not. -->

Closes #N

- Design: <!-- docs/design.ja.md §x.y / N/A -->

## Verification

<!-- Commands that ran and their result. -->

- [ ] `./build.ps1`
- [ ] NativeAOT publish (warnings / executable size): <!-- required when the PR changes a src/ project's public surface, a dependency, or interop, whatever the Issue's Acceptance says (.cursor/rules/aot.mdc); N/A otherwise -->
- [ ] Searches behind any "every X does Y" claim: <!-- N/A -->

## Measurements

<!--
  Frame-path or data-path changes: time per stage, allocations per frame, and bytes uploaded to the GPU per frame, before and
  after, at the data sizes (points) and viewport sizes (pixels) the Issue names, against a named baseline. N/A otherwise.
-->

N/A

## Risk / Rollback

- Risk:
- Rollback:

## Checklist

- [ ] Title follows Conventional Commits (`type(scope): subject`)
- [ ] CSharpier applied (`dotnet csharpier format .`)
- [ ] New public API has XML docs; new tests have `summary` + `remarks` (Preconditions / Steps / Expected)
- [ ] `docs/`, `.cursor/rules/`, and skills updated when behavior or workflow changed — or N/A
- [ ] Corrections from the owner written into the file they belong to (`.cursor/rules/record-corrections.mdc`) — or N/A
