# Editing an order writes no stock movement, and checks no stock

- **Started:** 2026-10-07 16:19
- **Branch:** fix/order-edit-stock
- **Kind:** fix
- **Status:** in progress

## Why

`OrderService.UpdateOrderAsync` adds and removes lines on an order that has already been
checked out, and touches nothing else. In production code `StockMovement` rows are written in
exactly two places — `CheckoutService.cs:149` (the write-off) and `InventoryService.cs:54`
(a delivery) — so an edit writes no journal row and moves no stock.

Two holes, not one, and the second is the worse one:

- **Nothing is written off.** Add a line and its ingredients never leave the shelf; remove a
  line and its ingredients stay written off although nobody made the dish.
- **Nothing is checked.** `CheckoutService.cs:96` refuses the whole checkout when a shortage
  exists; the edit path has no such check at all, so a line whose ingredients are gone can be
  saved onto a live order with no word from the app. The recipe warning
  (`WarnAboutMissingRecipesAsync`) is checkout-only too.

The drift is not contained, because the reversal on cancellation reads the journal rather than
the recipe — deliberately, and pinned by `The_stock_reversal_returns_what_was_written_off_not_what_the_recipe_says_now`.
Once an order has been edited, its journal is permanently out of step with it, and cancelling
returns the wrong amounts:

- two lattes down to one returns the milk for **two** cups, one of which was made;
- one latte plus an espresso returns **nothing**.

The owner edits orders occasionally, so this is not a fire. It is a counter that quietly stops
counting, and it is also the thing that would make the next planned feature — a "N portions
left" badge on the menu tile — read a number that is not true.

## What changed

**A stock delta for the edit path, computed as a decision and applied in one transaction.**

- `Core/Common/StockDeltaPlan.cs` — the decision, pure and unit-testable: from the line set
  before and after the edit, the signed per-ingredient deltas and the shortages. Same shape as
  `BundlePlan` and the other pure types in `Core/Common`; nothing in it knows about EF.
- `StockPlan.WriteOffDelta` (or an equivalent on the planner) — applies those deltas and
  returns the journal rows, mirroring `WriteOff`'s signature.
- `OrderService.UpdateOrderAsync` — builds a stock plan for the incoming lines, refuses the
  whole edit on a shortage, writes the movements, and does all of it inside one transaction so
  a refusal leaves the order untouched.
- The reversal guard in `StockPlan.ReverseAsync` narrowed. It currently refuses to return stock
  when the order has **any** positive movement carrying an `OrderId`, and its own comment says
  why: nothing in the app wrote one. After this change an edit *can* write one, so the rule as
  written would make a later "cancel with stock returned" impossible on every edited order. The
  rule becomes "a delivery booked against this order", decided on a typed reason rather than by
  parsing the `Reason` string — the same instinct as §5's integer kopecks, where a string
  comparison is what breaks silently.
- A schema migration for the typed reason, and the reason columns' widened if the size needs it.

**Not in this branch, on purpose:** the portions badge, any undo, manual discounts, the shift
operator. See `docs/PLAN-stock-and-undo.md` §4 for the order and why this one goes first.

## How it was verified

- **Red first, and eight of eleven were genuinely red.** On the unmodified code
  `dotnet test Tests/CafePos.Tests.csproj --filter "FullyQualifiedName~OrderEditStockTests"`
  reported `Failed: 8, Passed: 3, Total: 11`, with the failures on quantity down, quantity up,
  removing a line, adding a line, a bundle line, the shortage refusal, the delivery-in-journal
  refusal and the recipe-drift refusal.
- **The ninth assertion was already green, and that is worth saying plainly.**
  `Cancelling_an_edited_order_returns_only_what_the_edited_order_took` passes today — because
  the edit moves nothing, so reversing the original write-off happens to be consistent. It is
  not evidence of the defect; it is the guard against a fix that writes a second full write-off
  instead of a delta, which would leave the shelf 200 ml too high and would have gone green.
- **Full suite: `Passed: 515, Failed: 0`** (490 before this change; +12 integration, +13 pure).
  `dotnet test Tests/CafePos.Tests.csproj -c Debug`.
- **Four expectation edits, all mechanical and none a weakening**, each forced by a new schema
  version: `SchemaMigrationTests` 13 → 14 in the latest-version and applied-count assertions, its
  backfill list extended with `Migration014_StockMovementKind().Name`, and `BackupTests` 13 → 14.
  No assertion was relaxed and none was deleted.
- **Release build: 0 errors**, `dotnet build CafePosApp.csproj -f net10.0-windows10.0.19041.0
  -c Release`. 8 XC0022 warnings, all `Picker.ItemDisplayBinding` — the pre-existing set, listed in
  §8. This branch touches no XAML.
- **Migration on a legacy database:** `Legacy_v1_database_is_upgraded_to_the_latest_version`
  covers v1 → 14, and `AddColumnIfMissingAsync` is a no-op when the table is absent.
- **Figures, not adjectives.** Latte at 200 ml/cup, shelf at 100 000 ml:
  checkout ×2 → 99 600; edit to ×1 → **99 800**; cancel with return → **100 000**, and the order's
  journal nets to exactly **0**. Before the fix the same sequence left the shelf at 99 600 after
  the edit and, had the operator edited again, diverged on every further cancellation.

## Device pass, and what it could not check

Migration 14 was run against the emulator's real database, which is at version 13 and has four
`InProgress` orders in it:

```
SchemaMigrator: Applying schema migration 14: A stock movement records what kind of row it is
ALTER TABLE [StockMovements] ADD COLUMN [Kind] INTEGER NOT NULL DEFAULT 0
SchemaMigrator: Schema upgraded to version 14 (1 migrations)
DatabaseBootstrapper: Database ready. Schema version 14
```

**The first attempt proved nothing and is worth recording.** The emulator was handed the APK
already sitting in `bin\Debug`, built at 12:04 — four hours before the migration was written. It
reported "Schema version 13" and would have looked like a failed migration. The Windows-target
builds do not produce the Android package; the app had to be rebuilt with
`-f net10.0-android`. A device pass against a stale binary is a pass for the wrong reason.

**The emulator's stock journal is empty, so it could not test the case that matters most.** Read
out of the device database: 0 stock movements in total, 0 with an `OrderId`, 4 `InProgress`
orders. Migration 14 defaults every pre-existing row to `Unknown`, and on the owner's own phone
every one of those rows IS a write-off. Had the guard treated "unknown" as suspect rather than
looking at the sign, the upgrade would have quietly broken stock returns for every order already in
the database — and the emulator would have shown nothing wrong.

So that case is now a test, and the test is mutation-checked:
`A_cancellation_still_returns_stock_for_journal_rows_from_before_kinds_existed` forces the row to
`Unknown` exactly as the migration's default does, then cancels with a return. Dropping the
`QuantityDelta > 0` clause from the guard fails it and
`An_empty_or_write_off_only_journal_describes_the_order`; both were watched fail, and the file is
byte-identical to the committed state afterwards.

## Not verified

- **No operator pass.** The edit screen itself is unchanged — no new control, no new message path
  in the UI — but the two new refusals carry operator-facing text that nobody has read on a
  screen. That is §3.3 item 4, and it is the owner's.

## What was rejected, and why

- **Returning stock by recomputing the recipe.** Rejected: this is the mistake the existing
  cancellation rule exists to avoid. The recipe has no versioning, so a recompute returns the
  recipe at edit time, not the recipe at write-off time, and invents ingredients.
- **Writing the edit as a fresh full write-off.** Rejected: the checkout already wrote one, and a
  second full write-off for the same order double-charges the shelf.
- **Fixing the badge first, the deficit second.** Rejected by the owner after the argument was
  made, and correctly: the badge is a number read from the shelf, so shipping it over an
  unfixed editor builds a visible feature on a counter that has already stopped counting.
- **Refusing the whole edit path instead.** Rejected: the owner edits orders to correct real
  mistakes, and removing the ability to remove a line is not a fix for a silent accounting gap.
- **Asymmetry — write off additions, never return removals.** Rejected: the order is still
  `InProgress`, so nothing has been made, and a removed line's ingredients are sitting on the
  shelf as written-off. Asymmetry would be a smaller diff and a wrong shelf.

## Rules this changed

- **`.opencode-rules.md` §5** — new bullets on editing an order: the correction's old side is the
  journal, a changed recipe refuses the edit, and a shortage refuses the whole edit. §5 said a
  write-off happens at checkout and is inverted at cancellation; this adds the third state the
  other two rules had to account for.
- **`docs/decisions/stock.md`** — `ReverseAsync`'s reason (1) rewritten, because it said an edit
  "never touches stock", which is what this change stopped being true of. Added the
  `StockMovementKind` section and marked the old `if` guard superseded, with why the narrower rule
  is a correction and not a weakening.
- **`.opencode-rules.md` §8** — the Release warning count said 6 and is 8. Measured on this branch,
  which touches no XAML, so the figure was stale rather than changed. The reasoning around
  XC0045 and XC0022 is untouched; §8's own warning about a stale rule is what §17.2 asks for.
