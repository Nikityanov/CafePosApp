# Merge, then delete the branch

- **Started:** 2026-10-07 13:05
- **Branch:** chore/delete-branch-after-merge
- **Kind:** chore
- **Status:** merged — `59a3905` (merge commit), `eb80981` (branch)

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
- **Deleting `fix/analytics-scrolling` too, to leave a tidy list.** Rejected on the first
  pass. It is the one branch that still matters: branched 2026-10-01, never merged, and
  carrying real work — *"the product breakdown on Аналитика showed three rows of a
  four-row list"*. So it was checked on the device instead, and the answer is below.
- **Committing `.opencode-rules.md`.** The owner overrode the "intentionally local"
  decision: the rules are now tracked, so a rule change is an ordinary reviewable commit.

## `fix/analytics-scrolling` is already fixed in `develop`, by another route

The owner asked to check it on the device before deciding. That check is the finding.

**The bug it fixed:** on Аналитика the product breakdown was the only scrolling field. Of a
1945 px content area the toolbar, eight KPI cards and the peak-hour card took 1073 px,
leaving the breakdown — the reason the tab exists — 478 px. A product row is 154 px, so
three rows fit and the fourth was unreachable without scrolling that one field alone.

**`develop` already has this fix, from a different commit.** Evidence, not impression:

- The breakdown is built with `BindableLayout` on a `VerticalStackLayout` —
  `SectionRows`, `Dishes`, `Lines`, `DishRows`, `LineRows` — which is exactly what the
  branch introduced. The page is one `ScrollView` (`ShiftAnalyticsPage.xaml:59`).
- **The reasoning is already in `develop`,** in the XAML comment at lines 26–41: *"a
  CollectionView inside a ScrollView needs an explicit HeightRequest guessed from an item
  count — wrong … BindableLayout on a VerticalStackLayout: the idiom already used on the
  shift report."* That is this fix's argument, already committed to the codebase.
- On the device: one continuous scroll, **14 позиций из 14**, all four dishes present —
  Американо ×9 + Капучино ×2 + Чизкейк ×2 + coffe and more ×1 = 14 across 3 sections, and
  the header total agrees with the rows.

So the branch is superseded: merging it would resurrect a layout decision `develop` has
already moved past. **It should be abandoned, not ported.**

One real difference, and it is a choice rather than a defect. The branch kept the shift
picker and «Обновить» **outside** the scroller, so they stay reachable while the breakdown
is in view. `develop` puts both **inside** it (`:59`, `:67`), so they scroll away. That is
consistent with how the page is built and documented, but it is worth deciding
consciously rather than inheriting from whichever branch landed first.

- §3.3 — deletion is step 6 of the merge, same sitting, both copies.
- §3.4 — new: delete without asking, and never `-D`.
- §3.5 — new: an unmerged branch rots, with this repo's `fix/analytics-scrolling` as the
  worked example.
- §3.6 — new: housekeeping command for finding clutter and undecided work.
- **The rules file itself is now tracked.** `.gitignore` no longer excludes it, the line in
  its own header that said "keep it gitignored" is replaced with the reason it changed, and
  `.github/copilot-instructions.md` became a pointer to it instead of a stale duplicate.
