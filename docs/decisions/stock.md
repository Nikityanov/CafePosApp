# Склад и остатки

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## StockDisposition

```csharp
public enum StockDisposition
```

What happens to the ingredients that were written off when an order is cancelled. The operator
picks this once per cancellation, because the two answers are genuinely different facts about
the bar, and neither of them can be inferred from the order:
<list type="bullet">
<item>`LeaveWrittenOff` — the goods were made and handed over, or the shelf and the
terminal disagree about what is there. Nothing goes back: the ingredients were consumed.</item>
<item>`ReturnToStock` — nothing was ever made (a mistake at the till, a customer who
left), so the write-off is undone and the ingredients come back.</item>
</list>
Stock is deliberately NOT restored automatically. On a discrepancy the count is already wrong,
and putting the shelf back the way the recipe said would restore a wrong number and hide the
discrepancy behind a clean audit trail.

## PreviewShortagesAsync

```csharp
public async Task<List<StockShortage>> PreviewShortagesAsync(IReadOnlyList<(Guid ProductId, int Quantity)> lines, CancellationToken cancellationToken = default)
```

Shortages for a cart, before it is sold.
The tuple shape is kept on PURPOSE, even though the planner behind it now takes expanded
`ProductDemand`s. This is the method the menu screen calls while the customer is still
standing there, and it has always answered for "the dishes on this cart". Bundles reach it the
same way they reach the sale — through `ComboExpander` — so the preview and the write-off
ask the planner about the same dishes and cannot disagree about what is missing.
The empty `Name` is not a placeholder that leaks: `BuildAsync` reads the identifier
and the quantity, and a shortage is described from the INGREDIENT's own row, so there is no
demand name on screen anywhere in that path.

## StockReversal

```csharp
public sealed record StockReversal(IReadOnlyList<StockMovement> Movements, IReadOnlyList<string> IrreversibleIngredients)
```

What a cancellation could not put back on the shelf. The names (or, for an ingredient whose row
is gone together with its journal, the identifiers) the operator has to be told about — the
alternative is telling them the stock came back when it did not.

## ReverseAsync

```csharp
public static async Task<StockReversal> ReverseAsync(
```

Undoes `WriteOff` by reading the StockMovement journal of the order and applying
the inverse delta. The mirror of the write-off, and deliberately NOT a recompute.
WHY a recompute would be a bug, not a simplification: a write-off is a recorded event, and only
an event can be reversed. (1) An order's journal is the sum of its checkout and of every edit made
since, so it — and not `order.Items` — is what the shelf was actually asked for; a recompute of
the stored lines would return what the recipe says today rather than what left the shelf.
(2) SaveRecipeItemAsync edits recipe quantities with no versioning, so the recipe at cancel time
is not the recipe at checkout.
Static and on the type rather than an instance method on a plan: it must NOT be given the
plan's Required/Ingredients, because reading those is exactly the recompute that is wrong
here. Nothing is saved — the caller's SaveChanges/Commit is the only commit point.

## if

```csharp
if (await db.StockMovements.AnyAsync(
```

SUPERSEDED BY `EnsureJournalDescribesTheOrder`, and the old guard became wrong the moment an edit
started moving stock. It refused to reverse when the order had ANY positive movement carrying an
OrderId, and its own justification was that nothing in the app wrote one. An edit does write one —
a removed dish puts its ingredients back — so the rule as it stood would have made "cancel with
stock returned" impossible on every edited order. What the guard now refuses is a positive row that
is not an edit: a receipt, or a row from before kinds existed.

The narrower rule is not a weakening. Negation is of the NET, and an edit row belongs to that net
by construction, so allowing it is what makes the returned amount right rather than larger.

## StockMovementKind

```csharp
public enum StockMovementKind { Unknown, WriteOff, Delivery, Edit, Reversal }
```

Migration 14 adds `StockMovements.Kind INTEGER NOT NULL DEFAULT 0`. Why a column and not a prefix
on `Reason`: the reason strings are the operator's text, the guard has to be exact, and §5 already
records what happens when a decision rests on a stored string — SQLite keeps it as TEXT,
comparisons behave alphabetically, and nothing says so. `Unknown = 0` is what a pre-migration row
gets, and it is honest: such a row is refused rather than guessed at.

`Delivery` carries no `OrderId` (`RestockAsync` leaves it null), which lets the rule be stated
without a special case: a POSITIVE row in an order's journal is a receipt. Sign beats kind — a
negative row is consumption whatever it was called.

## Add

```csharp
irreversible.Add(row.IngredientId.ToString());
```

StockMovements.IngredientId is ON DELETE CASCADE and ingredients are a HARD delete
(they carry no IsDeleted flag, unlike Products), so deleting one destroys its
journal rows: there is nothing left to invert and no name left to report — only
the identifier. CatalogService.DeleteIngredientAsync refuses to delete an
ingredient that has movements precisely so this state cannot be created from the
app; this branch is what legacy and hand-edited databases report. Silently
skipping would tell the operator the stock came back when it did not.

## AddRange

```csharp
db.StockMovements.AddRange(movements);
```

Through the DbSet, never order-by-hand into a loaded collection: the same trap
PaymentRecorder's doc describes — an entity pushed into the collection of a tracked parent
is tracked as Modified and EF then UPDATEs a row that does not exist yet.

## BuildAsync

```csharp
public static async Task<StockPlan> BuildAsync(
```

What has to be written off for a cart, as dishes rather than as cart lines.
**THE INPUT IS EXPANDED, NOT A LIST OF LINES.** It used to be
`(ProductId, Quantity)` pairs taken straight from the cart, which meant a bundle arrived
here as itself — and a bundle has no recipe, because it is a rollup rather than a dish, so its
ingredients were never looked up and never written off. Taking the expanded demands instead
(see `ComboExpander`) is what lets a bundle's slots reach the same recipe lookup as any
other dish, with no second write-off engine and no change to anything below this line.
`ProductDemand` rather than a bare pair because the shortage message needs the dish's name,
and a shortage a cashier cannot name is a shortage they have to go and look up.
Duplicates are expected and are summed below: two lines of the same dish, or two slots of the
same dish inside one bundle, are one ingredient demand and must add up rather than overwrite
each other.

## GetValueOrDefault

```csharp
required[item.IngredientId] = required.GetValueOrDefault(item.IngredientId) + item.Quantity * demand.Quantity;
```

"+=" and not "=": the same ingredient reached through two dishes, or through the same
dish twice, is two withdrawals from the shelf. Overwriting here would deduct one of
them and the stock would drift up by exactly the amount of the one that vanished.
