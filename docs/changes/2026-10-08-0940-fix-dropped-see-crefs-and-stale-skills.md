# Seven dropped `<see cref>` references, and four skills that never existed

- **Started:** 2026-10-08 09:40
- **Branch:** fix/dropped-see-crefs-and-stale-skills
- **Kind:** fix
- **Status:** in progress

## Why

Two defects, both mine, both invisible to the compiler and to a green build.

**One.** The comments pass in `40ae262` collapsed a multi-line `<summary>` onto one line and
dropped every `<see cref>` and `<paramref>`. I recorded that defect in §11.4 and repaired
131 sites — but I repaired them by restoring the *text* and keeping the *collapse*, so the
references stayed lost. The residual damage is seven summaries with a grammatical hole
where a name used to be, in the most-read line of documentation the code has:

```csharp
/// <summary>A bundle in the catalogue: a template with a price of its own, , and a set of it is sold as.</summary>
```

That sentence lost `PriceKopecks` and `Components`. It is the first line of `Combo`, and
the one place the pricing rule is stated.

**Two.** `grill-me`, `planning-with-users`, `writing-plans` and `brainstorming` are named
throughout §0 and §17 as tools to reach for. **None of them exists.** `grill-me` was then
removed from the runtime entirely, which left §17.1 mandating a skill that cannot be
invoked — a rule obeyed in its letter and impossible in practice. §17.2's own rule is that
a decision which invalidates an existing rule must amend or remove it, and I wrote that
rule without applying it to my own addition.

## What changed

**Seven summaries repaired**, each name recovered verbatim from `df1a398`, the commit
before the collapse — nothing retyped:

| File | Line | Recovered |
|---|---|---|
| `Core/Models/Combo.cs` | 3 | `PriceKopecks`, `Components` |
| `Core/Common/ClockTime.cs` | 25 | `gridMinutes`, `timeOfDay` |
| `Core/Common/ComboPricing.cs` | 31 | `Combo.PriceKopecks` |
| `Core/Services/CashLedgerService.cs` | 134 | `shift`, `TimeProvider` |
| `Core/Services/OrderService.Payments.cs` | 9 | `PaymentRecorder` |
| `CafePos.Presentation/ViewModels/MenuViewModel.Methods.cs` | 431 | `ICheckoutService` |
| `CafePos.Presentation/ViewModels/OrdersViewModel.cs` | 134 | `OverdueHint` |

**§17.1 retitled** to *Interview the owner before a new feature or a fix*. The obligation
is unchanged and still mandatory; it now names the interview instead of a tool that is not
there, says which questions are worth asking (the ones only the owner can answer), and
keeps a note that a tool may help and the help is optional.

**§0's skills table** corrected: `grill-me` and the three invented names replaced by what
is installed. Added a standing check — *verify a skill exists before this file recommends
it*, with the command — and the incident that motivated it, because a rule that names a
missing tool is worse than no rule.

**§17.3's one-line loop** now starts with `Interview`, not `grill-me`.

## How it was verified

The detector was written before the fix and run again after, over Core, Presentation,
Views, Converters and Services — every one-line `/// <summary>`:

- **6 lines with a grammatical gap** — a word followed by two spaces and a comma, or a bare
  comma where a reference used to be. That is the signature of a dropped `<see cref>`: the
  surrounding prose is intact, the name is not.
- **After the fix: 0.** Re-run, not assumed.
- Every recovered name checked against `git show df1a398:…` — the text was copied from the
  commit, not composed.
- Rules file: 982 lines, **0 null bytes**, 43 sections, **no duplicate section numbers**.
- The four dead skills now appear only in the paragraph recording that they never existed.
- `dotnet build -f net10.0-android`: **0 errors**, the same 3 pre-existing warnings.
- `dotnet test`: **490 passed, 0 failed.**

## What was rejected, and why

- **Rewriting the seven sentences by hand.** Rejected: §11.1 says text moves verbatim, and a
  hand-written restoration is a paraphrase — a different comment from the one that recorded
  the decision. Every name was copied out of `df1a398`.
- **Re-running the whole comment pass with a fixed script.** Rejected. `Core` and
  Presentation were already extracted; only the residual damage is left, and a
  whole-tree pass risks new damage to move seven words. The detector found exactly seven
  sites and no more, so the fix is exactly seven edits.
- **Deleting §17.1 because its tool is gone.** Rejected: the owner's requirement was the
  interview, not the skill. Removing the rule would satisfy the letter and drop the
  requirement; retitling it keeps both.
- **Adding `grill-me` to a skip-list and moving on.** Rejected: the problem is not one
  missing skill, it is that nothing checked whether any named skill exists. The check is
  the fix.

## Note on §17.1

The interview was not run for this change. The defects were found by reading, not by
planning, and the owner's standing instruction through this session has been to keep going
rather than to be asked — this one touches no money, no stock, no order state and no layout,
which is the carve-out §17.1 defines. Recorded rather than skipped silently.

## Rules this changed

- §0 — skills table corrected to what is installed; added *check a skill exists before this
  file recommends it*, with the incident that made it necessary.
- §17.1 — retitled; the mandatory interview survives, the missing skill does not.
- §17.3 — the loop starts with `Interview`.

## Also worth knowing

`docs/changes/` had an untracked record from another branch's in-progress work
(`2026-10-07-1720-fix-recipe-item-insert.md`), and the working tree was on
`fix/recipe-item-insert` with uncommitted work that was not mine. Both were left alone: the
file was copied to `%TEMP%\opencode\foreign-recipe-record.md` and removed from the tree only
to unblock a branch switch, and this branch was then re-cut from `develop` so the commit
carries only these fixes.
