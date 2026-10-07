# Delete fix/analytics-scrolling, superseded

- **Started:** 2026-10-07 14:05
- **Branch:** chore/drop-superseded-analytics-branch
- **Kind:** chore
- **Status:** in progress

## Why

The last remaining stale branch, branched **2026-10-01** and never merged. The owner
authorised its deletion once the check on the device had come back. §3.5 predicted this
exact outcome: the branch rotted until the files around it moved.

## What changed

`fix/analytics-scrolling` deleted, locally. It was never pushed, so there is no remote copy.
Its one commit, `5d4c850` — *"the product breakdown on Аналитика showed three rows of a
four-row list"* — is unreachable from `develop` afterwards, deliberately.

## How it was verified

The deletion is only safe if `develop` genuinely carries the fix, so all three parts were
checked rather than assumed:

**1. The layout fix is in `develop`.** `ShiftAnalyticsPage.xaml` builds the breakdown with
`BindableLayout` on a `VerticalStackLayout` at six sites — `SectionRows`, `Dishes`,
`Lines`, `DishRows`, `LineRows` — inside a single `ScrollView` at line 59. On the device:
one continuous scroll, **14 позиций из 14**, all four dishes present (Американо ×9 +
Капучино ×2 + Чизкейк ×2 + coffe and more ×1 = 14 across 3 sections; the header total and
the rows agree).

**2. The reasoning is in `develop`.** The XAML comment at lines 26–41 already argues this
fix's case: *"a CollectionView inside a ScrollView needs an explicit HeightRequest guessed
from an item count — wrong … BindableLayout on a VerticalStackLayout: the idiom already
used on the shift report."*

**3. The branch's one piece of code is superseded by something better.** This was the part
that needed a second look, because the branch's 8 added lines are not a comment:

```csharp
public bool HasProductRows => ProductRows.Count > 0;
// and, after SyncWith:
OnPropertyChanged(nameof(HasProductRows));
```

`develop` has no `HasProductRows` — and does not need it. It has `HasNoProductRows` as a
**backed field with `SetProperty`** (`ShiftAnalyticsViewModel.cs:119-127`), assigned where
the rows are rebuilt (`ShiftAnalyticsViewModel.List.cs:274`) and notified at `:278`. Two
things it does that the branch's version does not:

- **The empty label says which kind of empty it is.** `EmptyListText` distinguishes a shift
  with no closed orders, a filter that excluded everything, and a search that matched
  nothing. The branch's hardcoded string could only ever state the first — which is exactly
  the defect the XAML comment at `:556-560` records as already fixed.
- **It notifies properly**, through `SetProperty`, rather than a read-only computed value
  whose change has to be announced by hand.

So the branch is not merely superseded on the layout; its code half was overtaken by a
solution to a **larger** version of the same problem.

**4. The paths have moved.** The branch edits `ViewModels/ShiftAnalyticsViewModel.cs`; that
file lives at `CafePos.Presentation/ViewModels/` on `develop` since the Phase 1 extraction.
`git merge-base` confirms `5d4c850` was never merged. Nothing on `develop` depends on it.

## What was rejected, and why

- **Force-merging it to "not lose the work".** Rejected: it would resurrect a layout
  decision `develop` moved past, reintroduce a `HasProductRows` next to a better
  `HasNoProductRows`, and conflict in two files to do it. Keeping it costs nothing;
  porting it costs a wrong answer.
- **Deleting without recording why.** Rejected. A deleted branch with no trace invites the
  same work being attempted again. This record is the trace, and §16 requires it before the
  branch goes.

## Note on §3.4

§3.4 says `-D` is never the answer and that if `-d` refuses, the owner decides. That is
what happened: `-d` refused because the branch is unmerged, the owner was asked and
authorised it, and the deletion was recorded here. The rule was followed, not bypassed —
the point of `-d` refusing is to force a conversation, and the conversation happened.

## Rules this changed

None. §3.5 predicted this outcome and §3.4 already covers it; this is the rule working,
not the rule changing. The only edit is this record and its row in `CHANGELOG.md`.
