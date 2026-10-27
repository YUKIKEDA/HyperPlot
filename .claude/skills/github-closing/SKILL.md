---
name: github-closing
description: >-
  Keep a GitHub issue open when a pull request does not finish it. Use when writing a PR body,
  a commit message, gh pr create, Closes, or any text that names an issue number.
---

# Closing keywords

GitHub completes an issue at merge when the PR title, body, or squash commit message contains a closing verb immediately before the issue number. Negation is still a match: `This does not close #12` completes #12.

Verbs: `close`, `closes`, `closed`, `fix`, `fixes`, `fixed`, `resolve`, `resolves`, `resolved`.

## Finish the issue

Under `## Related`, one line, nothing else on it:

```text
Closes #12
```

Do not repeat that verb next to the same number anywhere else, and never in a commit body.

## Leave the issue open

Write `#12` with none of those verbs before it.

```text
Part of #12. The NativeAOT check on #12 is still unmet.
```

## Before sending

1. Read the title and the full body. For each issue number, decide whether this PR finishes it.
2. If it does not, and a closing verb is the word before the number, rewrite the sentence.
3. Run `gh pr create` (or `gh pr edit`).
4. Run `gh pr view <n> --json closingIssuesReferences` and compare the list with step 1.

A PR stacked on another branch shows no closing references: GitHub links them only for a PR into the default branch. Check the list again when the PR is retargeted to `main`.
