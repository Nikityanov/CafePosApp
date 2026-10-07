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

Filled in as the work lands. Required before merge:

- a red test first: `UpdateOrderAsync` currently writes no journal row, so the assertion that it
  does has to fail before the fix and pass after it;
- `dotnet test Tests/CafePos.Tests.csproj` green with **no expectation edits** — the existing
  guard test
  (`Cancelling_refuses_to_return_stock_when_the_order_has_a_receipt_in_the_journal`) keeps
  passing, which is what proves the narrowing did not turn into a removal;
- a migration test from an older database to the new version;
- the cancellation-after-edit numbers stated as figures, not as "stock looks right".

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

To be filled in at merge (`.opencode-rules.md` §17.2). One candidate is already visible: §5's
domain invariants say a stock write-off happens at checkout and is inverted at cancellation, and
this change makes "edited order" a third state that the other two rules have to account for.
