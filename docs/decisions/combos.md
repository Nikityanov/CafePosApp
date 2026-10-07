# Комбо и состав

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## BundlePlan

```csharp
public sealed record BundlePlan(
```

A catalogue bundle read against what is on the shelf right now: what each slot may hold, what is in
it already, what the slots come to à la carte — or the dish that stops it being sold at all.
<param name="Options">The dishes a slot may hold, in catalogue order. Empty when blocked.</param>
<param name="Selection">The slots as they stand, in the same order. Empty when blocked.</param>
<param name="ReferenceKopecks">The à la carte sum of the slots. Zero when blocked.</param>
<param name="BlockedDishName">
The dish that cannot be sold, named; `null` when the bundle may be sold at all. A name rather
than a bool, because the cashier has to be told WHICH dish to replace in the catalogue.
</param>

**THIS DECIDES WHETHER A BUNDLE MAY BE SOLD, AND IT USED TO BE UNTESTABLE.** It was a private
nested record inside `MenuViewModel`, with three private helpers beside it, so nothing outside
the class could ask it a question. That is a poor place for the one rule that stands between a
cashier's tap and selling something the kitchen cannot make.
**WHY IT IS PURE, AND WHY THAT IS THE DESIGN.** It reads a `Combo` and its loaded
slots and returns a verdict. It never awaits a sheet and never touches the cart — the ViewModel
drives the UI and mutates the cart, exactly as it already does for an ordinary dish through
`ProductAddFlow`. One pattern for both kinds of add, so there is no second mechanic to
keep in step.
**SUBSTITUTION RATHER THAN BLOCKING.** The dish each slot offers is what would ACTUALLY be sold
for it — its own, or the declared substitute when its own has run out. Substitution rather than
blocking is what every serious vendor does (Simphony substitution groups, D365 product
substitutes). The authoritative answer is still `IComboService.ResolveSaleCompositionsAsync`,
called again on the tap: this is what the tile and the sheet are built from, not a second verdict
that could disagree.
**THE SUM IS THE À LA CARTE REFERENCE, NOT THE PRICE.** The bundle's own price is
`Combo.PriceKopecks`, read off the template by the caller. This sum is what the
discount is measured against — it never reaches the customer.

## Describe

```csharp
public static BundlePlan Describe(Combo template)
```

Ordering goes through `ComboComposition.CatalogueOrder`, which owns the rule, so
the tile's composition line, this sheet and `BundlePlan` cannot disagree about which
dish comes first.

## unit

```csharp
var unit = product.PriceKopecks;
```

The dish's price, not the slot's stored override — see ComboFormViewModel.UnitKopecks.
`reference` is the «по отдельности» figure the discount is quoted from, so a stored price
the operator can no longer see or change must not feed it.

## static

```csharp
public static (Product Product, string Label)? SellableDish(ComboComponent slot)
```

**PUBLIC, BECAUSE THIS RULE WAS WRITTEN TWICE.** The menu's bundle add and the order editor's
composition edit each had a private copy of this method. They agreed by luck: the same
predicate and the same «(замена: …)» label in both. One edit to one copy and the till would
compose bundles one way and the order editor another, with nothing failing and nothing saying
so. There is one copy now.

## SoldOutName

```csharp
private static string SoldOutName(ComboComponent slot) => slot.Product?.Name ?? slot.ProductId.ToString();
```

The dish that blocks a bundle, named. Falls back to the identifier when the product row is gone
— which a Restrict FK should make impossible from the app and a hand-edited database can still
produce. An identifier in a refusal is read by nobody, but it is traceable to a catalogue row,
where "a dish from the bundle" would not be.

## ComboComposition

```csharp
public static class ComboComposition
```

**WHY THE TILE NEEDS THIS AT ALL.** A bundle printed only its name and its price — «coffe and
more — 350,00 ₽» — so the only way to learn what was in it was to open the composition sheet. That
answers no question a customer actually asks. «Что входит?» is asked at the counter, with the
customer standing there, and it is answered from the tile.
**NO PRICES, AND NO MULTIPLIER UNLESS IT IS NOT 1.** The operator naming a dish needs identity,
not arithmetic: the bundle's price is already on the tile and the per-dish price is in the sheet,
and a tile that repeated the arithmetic would be a receipt, not a menu. The multiplicity is
different — «2 × Круассан» and «Круассан» are different bundles — so it is printed when it is above
1 and omitted otherwise.
**THE SLOT'S OWN DISH, NOT WHAT WILL ACTUALLY BE SOLD FOR IT.** A slot whose dish has run out is
being sold as its declared substitute, and the editor sheet names that substitution because the
cashier has to decide about it. The tile is not where that decision is made, the substituted name
is twice as long, and what a customer standing at the till is asking about is what the bundle
<i>is</i>. The tile also already carries the stock state that does change the answer — it dims and
names the dish outright when the bundle cannot be sold at all.
**WHY THIS IS IN CORE AND NOT IN THE VIEWMODEL FOLDER.** It sat beside
`MenuComboViewModel`, which made it unreachable from a test and unreachable from
`BundlePlan`, the rule that decides whether a bundle may be sold at all — so the tile
and the sale could not have shared one definition of "which dish comes first" even if both had
wanted to. Nothing here touches MAUI, so there is no reason for it to be anywhere but here.
The whole thing is one pass over the components the bundle was loaded with.
`GetCombosAsync` already loads every slot with its `Product` attached (see
`ComboService.LoadAsync`), so this costs no query at all — the names were on hand to compute
the price and are now read twice.

## Obsolete

```csharp
[Obsolete("The combo tile is full width and wraps; there is no character budget. Kept only to name what was removed and why.", false)]
```

How many characters the composition line may use. GONE WITH THE 208dp TILE.
The tile is full width and wraps now, so there is no budget to keep and no «и ещё N» tail to
measure. A character budget here existed only because a half-width card had to fit the
composition on one line, and it produced exactly the wrong thing: a bundle the cashier could
not read.

## CatalogueOrder

```csharp
public static IOrderedEnumerable<ComboComponent> CatalogueOrder(IEnumerable<ComboComponent> components) =>
```

Ordered HERE and not off the loaded collection, because `ComboComponent` carries no
`SortOrder` and the query behind it applies no `ORDER BY`: the collection's order is
whatever the join produced. This is the one place the order is defined, and the tile, the
composition sheet and `BundlePlan` all go through it, so none of them can disagree
about what the first dish in a bundle is.

## Summarise

```csharp
public static string Summarise(IEnumerable<ComboComponent> components)
```

NO LONGER TRUNCATED, and that is the owner's decision. The tile used to be a 208dp card in a
horizontal strip, which forced a character budget and printed «Американо, и ещё 2» — a bundle
whose contents the operator could not read, on the one screen whose purpose is naming dishes to
a customer standing at the counter. The tile is full width now and wraps, so every component is
printed. A hidden dish is a worse defect than a taller page.

## ProductDemand

```csharp
public sealed record ProductDemand(Guid ProductId, int Quantity, string Name);
```

One dish the stock planner has to write off, with how many and what it is called.
<param name="ProductId">The dish.</param>
<param name="Quantity">How many of it, across every cart line that asked for it.</param>
<param name="Name">Its name, so a shortage can be reported the way the operator ordered it.</param>

## ComboExpander

```csharp
public static class ComboExpander
```

**WHY A BUNDLE IS EXPANDED RATHER THAN GIVEN ITS OWN WRITE-OFF ENGINE.** Toast Recipes and
Restaurant365 both cost a bundle analytically and neither deducts ingredients per component: the
real write-off has no precedent, and an engine built for it would be a second implementation of
recipe arithmetic, in a domain that already has one and already gets it right. So the bundle is
unrolled to the shape `StockPlanner.BuildAsync` takes, and every existing shortage check,
clamping and reversal keeps working without knowing that bundles exist.
The bundle itself contributes no demand. It is a rollup, not a dish with stock — a combo has no
recipe and no 86 of its own, and counting it as well would double the write-off the moment anybody
gave it one.
**A KNOWN LIMITATION, WRITTEN HERE SO IT IS NOT REDISCOVERED AS A BUG:** an ingredient that is
sold on its own AND is in two bundles is deducted correctly by the formula, but no invariant of
the form "how many complete bundles can be assembled right now" holds — Shopify's community
documents the same thing. Stating that now is cheaper than being asked why the shelf disagrees.
The fix is a separate task and a different question from the one this answers.

## DescribeComponentsWithoutRecipe

```csharp
public static IReadOnlyList<string> DescribeComponentsWithoutRecipe(
```

Which bundle components have no recipe, so nothing will be written off for them. A WARNING and
not a refusal, and the distinction matters: blocking the sale would turn a missing recipe row
into a till that cannot sell coffee, while saying nothing leaves the shelf quietly diverging
from the books — the quiet part is the damage. Toast keeps an 86 report for exactly this
reason.
Ordinary dishes are not reported: one without a recipe has always been sellable in this app,
and warning about them would bury the bundles inside a list of everything.
Takes the set of dishes that DO have a recipe rather than reading the database, so this stays a
pure function that can be reasoned about and tested on its own; the caller already has to load
the ids for the planner's own query.
<param name="lines">The cart about to be sold.</param>
<param name="productsWithRecipe">Ids of the dishes that have at least one recipe item.</param>

## ComboComponentPrice

```csharp
public sealed record ComboComponentPrice(int QuantityPerUnit, long UnitKopecks)
```

One slot of a bundle as the à la carte reference is counted: how many of a dish per unit, and what
that one would cost on its own.
<param name="QuantityPerUnit">How many of this dish one unit of the bundle takes. Always >= 1.</param>
<param name="UnitKopecks">
What one of them is charged inside the bundle, in kopecks — already resolved, because on the sale
side "the dish's own price" has stopped being an option.
</param>

Kopecks, never rubles: the sum is the à la carte cost of a cart line, and every other price in the
order is an integer count of kopecks. Converting to `decimal` here and back would be a
rounding step in the middle of the one calculation whose exactness is the reason the column is an
INTEGER.
What this sum is NOT, since that is the whole point of the reversal: it is not the price of the
bundle. `Models.Combo.PriceKopecks` is.

## ComboPriceRelation

```csharp
public enum ComboPriceRelation
```

Three states and a fourth one, and the fourth is why this is an enum rather than a sign the caller
has to interpret: a bundle priced BELOW its parts is a discount, ABOVE them is a surcharge, equal is
neither, and a bundle whose parts add up to NOTHING cannot be compared at all. A signed percentage
covers the first three and cannot express the fourth, and a surcharge reported as a "−5% discount"
is exactly the dishonest label the plan forbids (с. 1.3: «наценка 5%», а не «скидка −5%»).

## ComboPricing

```csharp
public static class ComboPricing
```

**WHY NOTHING HERE COMPUTES A PRICE ANYMORE.** The sum of the slots used to BE the price, which
meant a dish price edit repriced every bundle containing it and nobody decided anything — the
program decided. The owner reversed it: the price is a number in the bundle's card, the sum is a
benchmark, and this class only counts the benchmark and compares against it. See
`docs/PLAN-combos-order-details.md` §1.3 and `Models.Combo`.
**WHAT THE REFERENCE IS STILL FOR.** It is what the report compares against — "was this bundle
cheaper than buying the parts?", a question the charged price alone cannot answer — and it is why
Simphony stores Price and Prep Cost side by side: both reports have to be buildable from one
transaction. The shift report's second, independent signal is
`Σ(ReferencePriceKopecks × QuantityPerUnit)` on the sold line, and this is where that figure
comes from at sale time.
**THE RESULT IS A CART LINE LIKE ANY OTHER**, which is why nothing in the order changed:
`Order.RecalculateTotal` stays `Σ(PriceKopecks × Quantity)` and a bundle of 3 is one
line of quantity 3, not three lines.

## ReferenceKopecks

```csharp
public static long ReferenceKopecks(IEnumerable<ComboComponentPrice> components)
```

The à la carte cost of one unit of the bundle: the sum of every slot, priced as the same dishes
on their own. A REFERENCE, not a price — it never reaches the customer.
Quantity N on a cart line means N complete bundles, so the per-unit multiplicity stays in the
slot and never enters this sum — that is the whole reason `ComboComponentPrice`
carries it as a property of the slot.

## TotalKopecks

```csharp
public static long TotalKopecks(IEnumerable<ComboComponentPrice> components) => ReferenceKopecks(components);
```

The old name of `ReferenceKopecks`, kept so the MAUI layer keeps compiling while it
is moved over.
**IT NO LONGER MEANS THE PRICE, AND NOBODY MAY READ IT AS ONE.** It is the same number, the
same arithmetic, the same result — the à la carte reference. Every caller that used it to price a
cart line is now pricing a cart line from the sum of its parts, which is precisely what
`Models.Combo.PriceKopecks` exists to stop. Migrate to
`ReferenceKopecks` and read the price off the bundle.

## DiscountPercent

```csharp
public static decimal? DiscountPercent(long referenceKopecks, long priceKopecks)
```

How much cheaper (or dearer) the bundle is than its parts, in percent, rounded to two decimals.
<param name="referenceKopecks">The à la carte sum, from `ReferenceKopecks`.</param>
<param name="priceKopecks">What the bundle actually costs.</param>
<returns>
A POSITIVE number when the bundle is cheaper than its parts, a NEGATIVE one when it is dearer
(a surcharge — legal, and never to be labelled a negative discount), exactly 0 when the two
agree — and `null` when the reference is zero.
</returns>

**THE ZERO REFERENCE RETURNS NULL, AND THIS IS THE POINT OF THE METHOD EXISTING.** There is no
percentage of nothing: dividing by zero would be an exception at best and a fabricated figure at
worst, and 0 is the worst of them all — a bundle priced 300 ₽ against free parts would be
reported as "0% cheaper than its parts", which is a false statement about a bundle that costs
the customer 300 ₽. A bundle with no reference has no percentage, and the caller shows
nothing. `Compare` says the same thing without arithmetic.
Display only. The percentage is never fed back into money: the charge is
`Models.Combo.PriceKopecks` in kopecks, and this is a rounded ratio of two kopeck
figures for a human to read.

## Compare

```csharp
public static ComboPriceRelation Compare(long referenceKopecks, long priceKopecks)
```

Where the bundle's price stands against its parts, as a named state rather than a sign the
caller has to interpret. A surcharge and a discount have to be tellable apart by the code that
draws the line, not by whoever reads the number afterwards.

## ResolveUnitKopecks

```csharp
public static long ResolveUnitKopecks(long? componentPriceKopecks, long productPriceKopecks) =>
```

What one slot contributes to the reference: the slot's own price when the catalogue states one,
otherwise the dish price.
The three states of `Models.ComboComponent.ComponentPriceKopecks` stay distinct
here, and collapsing any of them would change what a bundle means: null takes the dish price, 0
is free, and a number is this price INSTEAD of the dish price rather than on top of it (Aloha's
"Item price" method, and what `ProductAddFlow.ResolvePrice` already does for a
chosen variant — Simphony's default, "Add Side Prices To Meal Price").
A SLOT PRICE NO LONGER REACHES THE CUSTOMER. This value now counts towards
`ReferenceKopecks` only; the charge is `Models.Combo.PriceKopecks`. A
free slot still means "free of charge in this bundle" as a catalogue statement about the
comparison, and nothing more.

## ComboService

```csharp
public sealed class ComboService(
```

**WHY SUBSTITUTION IS RESOLVED HERE AND NOT IN THE CHECKOUT.** Doing it where the catalogue is
loaded means the slots, their dishes and their substitutes are read once and every figure in the
sale comes from that one read, so the receipt, the stock write-off and the price control cannot
disagree about what was sold.
**AND WHAT SUBSTITUTION NO LONGER DOES.** It used to re-price the bundle: D365's rule is "if the
replacement is dearer, the kit price is recalculated", and when the price WAS the sum of the slots
that fell out of the arithmetic for free — nobody had written the rule, it was just what a sum
does. A bundle now has a price of its own (see `Combo`), so that rule has nothing left
to attach to, and that is the correct outcome rather than a missing feature: a café that sells a
300 ₽ breakfast does not owe the customer 40 ₽ because the bread ran out, and if the difference is
worth recovering the answer is to change the price of the bundle on purpose, where accounting can
see it. The substitute is still written to the line's composition snapshot with its own price,
because the kitchen prints from that row and an audit six months later has to know what was
actually served.

## return

```csharp
return combos
```

Sorted in memory like every other ordered catalogue list in this codebase: SQLite cannot
ORDER BY a DateTimeOffset, and the ordering a catalogue screen shows has to be the ordering
one place defines (see CatalogService.GetCategoriesAsync for the same reason).

## if

```csharp
if (combo.PriceKopecks <= 0)
```

THE PRICE IS REQUIRED AND MUST BE POSITIVE. It is what the till charges, and a sellable item
at no price is a catalogue error rather than a bargain: a 0 here is a free meal that nobody
decided to give, and it is exactly the state a half-filled form produces. A bundle with no
slots is refused for the same reason — there is nothing to sell.

## ValidateReferencesAsync

```csharp
private static async Task ValidateReferencesAsync(AppDbContext db, Combo combo, CancellationToken cancellationToken)
```

Refuses a slot that points at a dish which is gone, soft-deleted, or (for a substitute) set to
nothing. Checked against the DATABASE rather than against the caller, because the caller is a
form that may have been open since before somebody deleted a dish — and a slot that survives
pointing at a deleted product is exactly the state the sale-side resolver then has to refuse,
with the operator looking at a bundle that looked fine when it was saved.

## SyncComponents

```csharp
private static void SyncComponents(AppDbContext db, Combo existing, IReadOnlyCollection<ComboComponent> incoming)
```

FULL REPLACEMENT of the slot set — see `IComboService.SaveComboAsync` for why a
partial write is not an option here. Slots are matched by identity so a slot that survives an
edit keeps its identifier, and the ones nobody mentioned are deleted.

## Add

```csharp
db.ComboComponents.Add(new ComboComponent
```

Through the DbSet, not the loaded collection: an entity pushed into the collection of
a tracked parent is tracked as Modified (its key is already assigned, so EF cannot
tell it is new) and EF then UPDATEs a row that does not exist. This trap is written
out at CatalogService.SyncVariants and PaymentRecorder.

## db

```csharp
await using var db = await factory.CreateDbContextAsync(cancellationToken);
```

The same loader the strict path uses, projected to the two columns this answer needs: an
order editor asks "what may this cost", not "what is in it", and it must not drag every slot
and both products along to find out. One read of one row per bundle.

## BundleIdsOf

```csharp
private static List<Guid> BundleIdsOf(IReadOnlyList<CheckoutLine> lines) =>
```

The distinct bundles named on these lines — i.e. of the lines that HAVE slots. One list for both
sale-side methods, because a rule written twice is a rule that will differ between the strict
path and the lenient one the first time somebody adds a case to it.

## ResolveOne

```csharp
private IReadOnlyList<CheckoutComponent> ResolveOne(Combo template, CheckoutLine line)
```

One line's composition, rebuilt from the catalogue. Everything the client sent about the slots —
names, quantities, prices — is dropped and replaced with what the catalogue says, so the sale,
the receipt, the stock write-off and the price control all read the same rows.

## ResolveSlot

```csharp
private CheckoutComponent ResolveSlot(ComboComponent slot)
```

One slot: which dish is actually sold for it, and what it counts for.
**SUBSTITUTE RATHER THAN BLOCK, which is what every serious vendor does** — Simphony
substitution groups, D365 product substitutes. Nobody takes a bundle off the menu because a side
ran out; the customer is served the replacement.
**AND THE PRICE DOES NOT MOVE WITH IT.** The old D365 behaviour — "if the replacement is
dearer, the kit price is recalculated" — is deliberately gone. It existed because the price WAS
the sum of the slots, so the difference arrived by arithmetic rather than by a rule anybody had
to write; a bundle has a price of its own now, and the difference between a 200 ₽ espresso and a
240 ₽ decaf is the café's cost of doing business, not an amount to be added to a customer's bill.
What the substitute is charged FOR here is the à la carte reference the bundle is measured
against, and it is recorded on the line so the kitchen and any later audit can see that this is
what was actually served.
**THE SLOT'S OWN PRICE OUTRANKS THE DISH'S.** `ComboPricing.ResolveUnitKopecks`
is applied to whatever dish ended up in the slot, so a slot priced explicitly (including a free
one) keeps its figure across a substitution — the slot's price is a decision about the bundle, not
about the dish that happened to be there. That is also plan 1.6's third case: a free slot's
unavailability must not change the price, while still gating whether the sale may happen at all.

## substituteName

```csharp
var substituteName = slot.SubstituteProduct?.Name;
```

No substitute, or the substitute is out too. Naming the dish is the whole message: a bundle
that will not sell is a catalogue or stock problem, and "комбо недоступно" leaves the
operator guessing which of four slots is the reason.

## SoldOutName

```csharp
private static string SoldOutName(ComboComponent slot) =>
```

The dish that is out, named. Falls back to the identifier when the product row is gone — which
a slot with a Restrict FK should make impossible from the app, and which a hand-edited database
can still produce. An identifier in a refusal is read by nobody, but it is at least traceable to
a catalogue row, where "блюдо из состава" would not be.

## Describe

```csharp
private static CheckoutComponent Describe(ComboComponent slot, Product product, long unitKopecks) => new(
```

The snapshot row for a slot: the dish that is ACTUALLY being sold under it, at the price that
slot is charged, with the same dish's à la carte price alongside it.
The name is the real dish's name, not the one the cart was holding: a substituted sale is printed
from this row, so calling the replacement by the name of the dish that ran out would put a
wrong name on a fiscal document. The same applies to the reference price — it is what THIS dish
costs on its own, which is the figure the "was the bundle cheaper than its parts" signal needs.

## IsSellable

```csharp
private static bool IsSellable(Product? product) => product is { IsAvailable: true, IsDeleted: false };
```

"Sellable" means available AND not soft-deleted. Both, because a soft-deleted dish is out of the
menu while its row — and therefore its price — is still there, and treating a deleted product as
sellable would quietly keep selling it.

## SaleComposition

```csharp
public sealed record SaleComposition(long? PriceKopecks, IReadOnlyList<CheckoutComponent> Components);
```

One line's sale-side truth: the bundle's own price and the composition the server is willing to
sell.
<param name="PriceKopecks">
What the till may charge for one unit — `Combo.PriceKopecks` from the catalogue — or
`null` for a line that is not a bundle.
</param>
<param name="Components">
The slots as the catalogue states them, with substitutes already applied. Empty for an ordinary
dish, which is what makes "is this a bundle" answerable without a flag.
</param>

The two figures together, because they answer two different questions and the caller needs both:
the price is what the customer pays, the composition is what the kitchen receives and what the
report measures the bundle against.

## IComboService

```csharp
public interface IComboService
```

The two halves live in one service because they answer one question — "what does this bundle
consist of, what may it be sold for, and may it be sold as it stands?" — and the sale half is the
only trustworthy source of the prices the price control compares against. A separate "sale
resolver" that read the catalogue its own way would be a second definition of a bundle, and two
definitions are how a bundle ends up priced one way on the menu and another way on the receipt.

## SaveComboAsync

```csharp
Task SaveComboAsync(Combo combo, CancellationToken cancellationToken = default);
```

Saves a bundle AND ITS WHOLE COMPONENT SET, including its price. The write is a FULL
REPLACEMENT: a slot missing from <paramref name="combo"/> is deleted.
This is not a convenience, it is the only safe shape. Square's upsert is full-replacement for
the same reason — a partial write that only adds and updates silently DESTROYS the children it
did not mention, so removing a component from a bundle would appear to do nothing and the bundle
would keep selling the removed dish. Removing something from a form must remove it from the
catalogue, or the operator will do it twice and then trust the screen even less.
`PriceKopecks` is PERSISTED here and is never recomputed from the slots. It is the number
accounting put in the card, and the sum of the slots is the à la carte reference it is measured
against — see `Models.Combo` and `docs/PLAN-combos-order-details.md` §1.3.
<exception cref="Core.Errors.ValidationFailureException">
No name, a price of zero or less, no slots, a slot pointing at a dish that is gone or
soft-deleted, a multiplicity below 1, a slot price below zero, or a substitute that does not
exist. Every one of them is a bundle that would sell for the wrong price or could not be sold at
all.
</exception>

## DeleteComboAsync

```csharp
Task DeleteComboAsync(Guid id, CancellationToken cancellationToken = default);
```

Soft delete: the bundle leaves the catalogue, and bundles already sold stay on their receipts
(their composition is a snapshot, not a reference). Hard delete would take the slots with it and
leave nothing for an audit to read.

## ResolveSaleCompositionsAsync

```csharp
Task<IReadOnlyList<SaleComposition>> ResolveSaleCompositionsAsync(
```

THE sale-side seam: takes the cart as the client asked for it and returns, index-aligned with
<paramref name="lines"/> and in the same order, what the bundle costs and the composition the
server is willing to sell.
<exception cref="Core.Errors.ValidationFailureException">
A slot is unavailable and has no usable substitute, or the cart asks for a composition the
catalogue no longer contains. Both name the dish, because "нельзя продать" without a name sends
the operator hunting through the menu.
</exception>

The client's own component names and prices are DISCARDED and rebuilt from the catalogue.
That is the whole reason this method exists and not a tidy-up: the price control in
`Common.OrderLinePricing` compares the charged price with the bundle's own price
and writes the composition onto a fiscal document, and both are worthless if the client gets to
pick them — a forged composition, or a cart price of its own choosing, would defeat it exactly.
So this is the only route by which a composition or an allowed price reaches a sale.
Substitution is applied HERE, and it moves the composition only: a dearer substitute is written
into the snapshot with its own price, and the difference is the café's to absorb rather than the
customer's. The café's decision about the price of a bundle is not re-opened by running out of
an ingredient.

## ResolveSalePricesAsync

```csharp
Task<IReadOnlyList<long?>> ResolveSalePricesAsync(
```

It exists for `OrderService.UpdateOrderAsync`, which adds a line to an order that is
already open and therefore has no checkout transaction to refuse in — the editor is allowed to
save whatever the order holds, including a composition whose template has since been deleted,
and the honest answer there is "the catalogue has no price for this line", not an exception in
the middle of a save. The sale path uses `ResolveSaleCompositionsAsync`, which is
strict; both read the same rows through the same loader, so the two cannot drift apart on what
a bundle costs.

## ResolveSaleComponentsAsync

```csharp
Task<IReadOnlyList<IReadOnlyList<CheckoutComponent>>> ResolveSaleComponentsAsync(
```

Kept because the MAUI layer still calls it, and because a caller that only needs the slots has
no business reading a price. **It says nothing about what a bundle costs**: the price is in
`ResolveSaleCompositionsAsync`, and reading it off the composition's sum brings back
the model the reversal removed.

## ProductAnalyticsProjectionResult

```csharp
public sealed record ProductAnalyticsProjectionResult(
```

Everything the list needs in one shape, for whichever grouping is active — see
`ProductAnalyticsGrouping`.
<param name="Sections">Populated for `ProductAnalyticsGrouping.SectionsAndDishes`.</param>
<param name="Dishes">Populated for `ProductAnalyticsGrouping.Dishes`; empty otherwise.</param>
<param name="Lines">Populated for `ProductAnalyticsGrouping.None`; empty otherwise.</param>
<param name="SectionNames">
Every section present in the unfiltered shift, in display order. The source for the filter
chips, which is why it is computed even when a section filter is already narrowing the list.
</param>
<param name="VisibleQuantity">Units across the rows the filter kept.</param>
<param name="VisibleRevenue">Money across the rows the filter kept.</param>
<param name="TotalQuantity">Units across the whole shift, before any filter.</param>

## ProductAnalyticsProjection

```csharp
public static class ProductAnalyticsProjection
```

IN CORE, AND PURE, ON PURPOSE. Every decision this type makes — which sections a dish belongs
to, what a section's total is, whether the filter hid anything — is a rule about the domain's
numbers, and a rule that lives in a ViewModel is a rule nothing can test. There is no MAUI type
anywhere in here: rows in, tree out.
It is a pure function of its arguments and holds no state between calls, so changing a filter
re-runs it from the SAME unfiltered rows rather than filtering an already-filtered list. That is
what lets the user un-check a chip and get the original order back.

## BuildSections

```csharp
private static ProductAnalyticsProjectionResult BuildSections(
```

Sections are grouped FIRST and dishes inside them, rather than dishes grouped flat and then
filed under a section afterwards. The order matters for correctness, not style: a dish name
is not unique — the same drink can sit in two sections — and grouping by name alone would
merge them into one header whose total is the sum of two unrelated dishes.
The sections come out in the CHOSEN ORDER, by the same measure as the dishes, not
alphabetically and not in first-seen order. A manager sorting by revenue is asking which
section took the money, and an alphabetical list of sections answers a question nobody asked.
The alphabetical order is not lost: it is `SectionNames`, which is what the
filter chips are built from.

## SectionComparer

```csharp
private static IComparer<ProductAnalyticsSection> SectionComparer(ProductAnalyticsSort sort) =>
```

Orders sections by the chosen criterion, tie-breaking on the name ALWAYS ascending — the same
rule the dishes follow, for the same reason. A section that ties another must not swap places
every time the direction is flipped.

## GroupDishes

```csharp
private static List<ProductAnalyticsDish> GroupDishes(IReadOnlyList<ProductAnalyticsRowData> rows, ProductAnalyticsSort sort) =>
```

The lines are ordered inside the group rather than globally afterwards: ordering lines across
dishes would interleave them, and then slicing the sequence back into dishes would scramble
them again. Ordering each dish's own lines once is the only way to get both correct.

## OrderDishes

```csharp
private static ProductAnalyticsDish[] OrderDishes(IEnumerable<ProductAnalyticsDish> dishes, ProductAnalyticsSort sort) =>
```

Orders dishes by the chosen criterion. Ties fall back to the name ALWAYS ascending, even when
the direction is descending: without it, two dishes sold the same number of times swap places
on every re-sort, and a list that reshuffles itself is read as noise rather than as a new
answer.

## if

```csharp
if (filter.Sections.Count > 0 && !filter.Sections.Contains(row.CategoryId)) return false;
```

An EMPTY section set means "no section filter", which is the state a cleared chip row
leaves behind. Treating it as "match nothing" would make clearing the filter empty the
list, and the user would have to re-pick every chip to get their data back.

## DishComparerAdapter

```csharp
private sealed class DishComparerAdapter(
```

The secondary measure and the name are both compared ascending regardless of direction, and
that is the whole reason this is a comparator rather than two `OrderBy` calls. A tie on
the primary measure has to resolve the same way every time: flipping the direction then
produces a list that is genuinely reversed, instead of one that is differently ordered among
the rows that happen to tie.
