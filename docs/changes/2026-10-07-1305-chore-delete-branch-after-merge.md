# Merge, then delete the branch

- **Started:** 2026-10-07 13:05
- **Branch:** chore/delete-branch-after-merge
- **Kind:** chore
- **Status:** in progress

## Why

The owner asked for one thing: after a merge, delete the feature branch, so branches do not
pile up. §3 already had the two delete commands at the end of the merge block, and they had
not been followed — six merged branches were sitting on the remote, the oldest from
**2026-10-02**, one week old.

That part is a habit and it is now a numbered step. But checking *why* those six were still
there turned up something more useful than the tidiness, and it is in §3.5.

## What changed

**§3.3 renamed and rewritten** — "Merge, then delete the branch — in the same sitting".
Deletion is now step 6 of the merge, with both copies called out, because the local delete
is the one people do and the **remote** delete is the one that actually accumulates on
GitHub.

**§3.4 added** — delete without asking permission, with the reasoning that `git branch -d`
is already the safety property: git refuses an unmerged branch, so after the merge commit
exists there is nothing to lose. `-D` is never the answer; if `-d` refuses, the branch is
genuinely unmerged and the owner decides. `master` and `develop` are never deleted.

**§3.5 added** — why an unmerged branch is expensive, with this repo's own example rather
than a principle.

**§3.6 added** — the housekeeping command, to be run before starting something new:
`git branch --merged develop` is clutter, `git branch --no-merged develop` is a decision
someone has not made yet.

**Deleted, six branches, local and remote:**

| Branch | Last commit | Remote |
|---|---|---|
| `chore/self-contained-comments` | 2026-10-04 | deleted |
| `feat/analytics-product-list-controls` | 2026-10-02 | deleted |
| `feat/cash-operations-and-drawer-reconciliation` | 2026-10-03 | deleted |
| `feat/currency-and-shell-chrome` | 2026-10-02 | **was never on the remote** |
| `fix/orders-list-and-cart-stepper` | 2026-10-02 | deleted |
| `fix/warning-text-contrast-and-maestro-harness` | 2026-10-02 | deleted |

## How it was verified

Not "git said so" — checked what that actually cost:

- **Every commit is still reachable from `develop`.** `git merge-base --is-ancestor` on all
  six branch tips: all six report *in develop*. Deleting a merged branch destroyed nothing.
- `git ls-remote --heads origin` confirms five branches gone from the remote, and confirms
  `feat/currency-and-shell-chrome` was local-only — so its one "error" was `git push
  --delete` having nothing to delete. Expected, not a failure.
- `develop` at `f0725f7`, in sync with `origin/develop`, working tree clean.
- **Left alone deliberately:** `fix/analytics-scrolling`, and reported to the owner instead
  of being force-deleted. See below.

## What was rejected, and why

- **`git branch -D` for speed.** Rejected: `-D` is how an unmerged branch gets destroyed
  silently. `-d` refusing is the check, and forcing past it throws the check away.
- **Deleting `fix/analytics-scrolling` too, to leave a tidy list.** Rejected. It is the one
  branch that still matters: branched 2026-10-01, never merged, and carrying real work —
  *"the product breakdown on Аналитика showed three rows of a four-row list"*, 68 insertions
  across `ShiftAnalyticsViewModel.cs` and `ShiftAnalyticsPage.xaml`. A tidy branch list is
  not worth losing a defect fix over, so it goes to the owner as a decision (§3.5).
- **Committing `.opencode-rules.md`.** Not possible: it is gitignored by its own declared
  choice, so this record is the only trace in the repo that the rules changed. Flagged to
  the owner as an open question, not solved unilaterally.

## Rules this changed

- §3.3 — deletion is step 6 of the merge, same sitting, both copies.
- §3.4 — new: delete without asking, and never `-D`.
- §3.5 — new: an unmerged branch rots, with this repo's `fix/analytics-scrolling` as the
  worked example.
- §3.6 — new: housekeeping command for finding clutter and undecided work.
