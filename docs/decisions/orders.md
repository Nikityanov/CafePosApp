# Заказы: правила и модель

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## OrderLineKey

```csharp
public static class OrderLineKey
```

**WHY IT IS ONE FUNCTION AND NOT FOUR PREDICATES.** The comparison was written inline in four
places, and three of them agreed while one did not: the cart, the add command and the order editor
all compared product + modifier + variant, but the order editor left the variant out — so adding
a large and a small of the same dish merged two lines that should have been separate. A key that
every caller computes the same way cannot drift that way again, and a mismatch is a failing test
rather than a receipt that quietly loses a line.
The component signature is part of the key, and that is what makes two bundles with the same dish
but different slots two different lines instead of one line at whichever price was added first.

## For

```csharp
public static string For(
```

A key for one cart line: product, modifier, variant and, for a bundle, its composition.
<param name="productId">The dish the line is for.</param>
<param name="modifier">Chosen modifier name, or `null` for none.</param>
<param name="variant">Chosen variant name, or `null` for none.</param>
<param name="components">
The slots of a bundle as (dish, count per unit). `null` or empty for an ordinary dish —
both mean "not a bundle", and both produce the same key, so an empty list cannot accidentally
split a plain dish from itself.
</param>

The caller is expected to compare keys with `string.Equals(string?, string?)` /
`==`, which is exactly what the existing merge logic does, so the value is built to be
compared and never parsed. It is not persisted and never shown.

## key

```csharp
var key = new StringBuilder(64);
```

Modifier and variant names are free text from the catalogue and may contain any character,
including the separator below. Each one is length-prefixed so that "AB" + "" cannot produce
the same key as "A" + "B" — which would merge two different dishes at two different prices
into one line and charge the quantity sum at one of them.

## signature

```csharp
var signature = components
```

Sorted by ProductId and THEN by quantity, because ProductId alone does not order two
slots of the same dish against each other: with equal ids the sort is not required to be
stable, so (A,1),(A,2) could fold as "1,2" on one tap and "2,1" on the next, and two
identical bundles would sit in two lines. The tie-break costs nothing and makes the
fold total.
The pairs are NOT added together: two slots of the same dish are two slots, and the sum
they contribute is the same either way (2 + 1 is 1 + 2 is 3), so merging them would buy
nothing while hiding which composition was actually sold.

## SeenAt

```csharp
public DateTimeOffset? SeenAt { get; set; }
```

**THIS EXISTS ONLY TO ANSWER ONE QUESTION: "HAS ANYONE SEEN THIS SINCE IT GOT READY?"**
`ReadyAt` alone cannot answer that — it says when the order changed, not whether
anybody noticed. The board draws an unread dot while
`Status == Ready &amp;&amp; (SeenAt is null || SeenAt &lt; ReadyAt)`, which is the Toast
pattern: a marker that waits to be acknowledged rather than one that fades.
Stored, not held in memory. The board rebuilds its rows on an auto-refresh tick, so an in-memory
flag would be lost every few seconds and on every restart — and a dot that forgets itself trains
the operator to ignore it, which is worse than having none.
Comparing the two instants rather than keeping a boolean is what lets the dot come BACK: an order
that goes back to preparing and then becomes ready again has a new `ReadyAt`, so
`SeenAt` now compares as older and the dot returns. A bool would have to be reset by
the status change itself, and every future path that moved an order to ready would have to
remember to.

## IsUnseenReady

```csharp
public bool IsUnseenReady(DateTimeOffset now) =>
```

A pure function of the two stored moments and the current status, so it cannot drift from them
the way a maintained flag can. Takes <paramref name="now"/> for the same reason
`IsScheduledAt` does: an entity has no clock, and a test must be able to place
itself on either side of the boundary.

## CustomerPhone

```csharp
public string? CustomerPhone { get; set; }
```

Contact for an `OrderType.Takeaway` order, in E.164 (see
`PhoneNumber.Normalize`) — or `null`, which is a normal state and not
a missing value.
**WRITTEN ONLY FOR TAKEAWAY, AND THAT IS A LEGAL REQUIREMENT, NOT A TIDINESS RULE.**
152-ФЗ ст. 6(1)(5) permits a phone to be processed only where it is needed for the performance
of the contract, and ст. 5(7) requires erasing it once that purpose is met. Counter service
does not need a number for anything — it is on the premises — so
`CheckoutService` drops it at the service boundary instead of writing a row it would
then have to erase. CoAP 13.11 ч.12 prices unlawful disclosure at 3–5 million ₽ for
1 000–10 000 subjects and counts ROWS, so the counter is how many rows exist, not whether the
column does.

## RequestedAt

```csharp
public DateTimeOffset? RequestedAt { get; set; }
```

The time the customer asked for, or `null` for "as soon as possible".
`null` is not an absent value that needs filling in: Toast models the same thing as a
lead time rather than as a flag, where `prep time = quote time + lead time`, and our
equivalent is `RequestedAt == null` with the promise counted as
`CreatedAt + `LeadTimeMinutes``. An "ASAP" flag would store the same absence
twice and the two could disagree.
It is deliberately not the same field as the promise. Oracle Communications OSM keeps
`RequestedDate` and `Promise Date` apart for the same reason: when they differ,
one of them is a mistake, and a single column cannot say which.

## LeadTimeMinutes

```csharp
public static int LeadTimeMinutes { get; set; } = DefaultLeadTimeMinutes;
```

How many minutes an order without a requested time is promised to take. A setting and not a
column, with 10 as the figure an ordinary drink is quoted at.
Ambient and mutable for the reason `Currencies.Default` is: an entity has no
constructor to receive it through, and `PromisedAt` is a derived property read
without a way to pass anything in. It is assigned once at startup from
`DatabaseOptions.LeadTimeMinutes` and never from the database, so no order row can change
it after the fact and a promise computed today cannot disagree with the figure quoted when the
order was taken.

## NotMapped

```csharp
[NotMapped]
```

When this order is meant to reach the customer: what was asked for, or otherwise the standard
promise counted from when it was created. Not stored, because it is arithmetic on
`RequestedAt`, `CreatedAt` and `LeadTimeMinutes` — a
stored copy would be a fourth place for the queue order and the report to disagree with.

## IsScheduledAt

```csharp
public bool IsScheduledAt(DateTimeOffset now) => RequestedAt.HasValue && RequestedAt > now;
```

Whether the customer asked for a time in the future, which is what puts the order in the
"by the time" section of the queue rather than in the working one.
Takes <paramref name="now"/> as an argument because an entity has no clock of its own: the
app's single clock is the injected `TimeProvider`, and passing the moment in keeps
this a pure function that a test can place on either side of the boundary.

## IsOverdueAt

```csharp
public bool IsOverdueAt(DateTimeOffset now) => PromisedAt < now;
```

Whether the promise has already passed while the order is still being made.
A display state and NOT a validation failure: no vendor rejects an order whose promised time
has elapsed in the queue — the kitchen is behind, which is a fact about the kitchen. It is
also a different measurement from the order's age in the queue; the two timers are kept apart
because a colour on the kitchen screen measures how long the item has been cooking, not how
late the promise is.

## PaidKopecks

```csharp
public long PaidKopecks { get; set; }
```

NET amount still held for this order, in kopecks: what was collected MINUS what was refunded.
Net is the figure every screen reads, because it is what the till still owes the customer —
after a partial refund the order is still paid for, just for less. The ledger reconciles
against it as `SUM(Where(!IsRefund)) − SUM(Where(IsRefund))`; the direction lives in
`OrderPayment.IsRefund`, never in a negative amount.
Persisted on purpose: every list that shows a payment state (GetActiveOrdersAsync, the orders
screen) loads orders WITHOUT their payments, and a value derived from the
`Payments` collection would read zero there — silently, with no error anywhere,
so every card would claim "unpaid" while the money is on the table. EF only links tracked
entities into a loaded collection during DetectChanges, so an un-Included collection is not
merely stale, it is empty.

## Payments

```csharp
public List<OrderPayment> Payments { get; set; } = new();
```

Ledger of the order. DISPLAY ONLY: populate it with an explicit `Include` (or on a
freshly added parent). It is intentionally absent from the queries behind the order lists —
nothing in the domain reads it, and `PaidKopecks` is the value every screen
must trust. See the remarks on `PaidKopecks` for what an un-Included collection
silently returns.

## RecalculateTotal

```csharp
public void RecalculateTotal(IEnumerable<OrderItem>? lines = null) =>
```

Recalculates `TotalKopecks` from the current items (integer arithmetic).
Pass an explicit line set when the new lines are not in `Items` yet — EF only
links a tracked entity into the loaded collection when it runs DetectChanges, and pushing
the same entity into the collection by hand would count it twice.

## IOrderCommands

```csharp
public interface IOrderCommands
```

Money is NOT here. `IOrderPayments` owns the ledger, because "the order moved on" and
"the till moved" are separate decisions with separate reasons to be wrong: a status can advance
without money (kitchen progress), and money can move without the status changing (a part-payment
against an order still cooking).
Every method here is a command — it writes, it may refuse, and it is not idempotent.
`IOrderQueries` is the read side.

## CancelOrderAsync

```csharp
Task CancelOrderAsync(
```

Voids a sale. A paid order is NOT refused: the money is refunded in full, mirrored back out
of the methods it arrived in, in the same transaction that flips the status — the two guards
this used to have ("the order is closed" and "the order is paid") are gone, because both of
them left a real cashier with no way to give a customer their money back.
<paramref name="stock"/> is the operator's one decision: leave the ingredients written off
(made, handed over, or a discrepancy) or return them to the shelf.

## MarkSeenAsync

```csharp
Task MarkSeenAsync(Guid orderId, CancellationToken cancellationToken = default);
```

Writes `Order.SeenAt` and nothing else. It is deliberately not coupled to
`UpdateOrderAsync` or to the status transition: the dot exists to answer "has anyone looked
since it became ready", and the answer must come from somebody actually opening the order, not
from whatever else happened to touch the row.
MONOTONIC, and that matters because the board auto-refreshes. A plain assignment would move
`SeenAt` backwards every time the operator reopened an order that had already been seen,
and a read older than `Order.ReadyAt` would bring the dot back for an order nobody
has touched since. `max` keeps the newest look.
Silent when the order is missing or already fully seen: this is a courtesy write on a path the
operator did not choose to enter deliberately, and a screen that failed because a dot was
already gone would be a worse board than the dot.

## AddContactDetailsAsync

```csharp
Task<Order> AddContactDetailsAsync(
```

Writes a phone and a promised time onto an order that has already been paid for — the customer
thought of it after the money changed hands.
<param name="orderId">The order to annotate.</param>
<param name="phone">Whatever was typed, or `null` to leave the number alone.</param>
<param name="requestedAt">The promised time, or `null` to leave it alone.</param>
<param name="promoteToTakeaway">
The operator's explicit agreement to change the order from counter service to takeaway because a
number was named. A phone on a counter-service order is REFUSED without this — see the remarks.
</param>
<param name="cancellationToken">Cancellation.</param>

**THE PHONE CONTOUR IS ENFORCED HERE, NOT IN THE SHEET.** 152-ФЗ ст. 6(1)(5) permits a phone
only where it is needed for the contract, and counter service needs none: the customer is on the
premises. `CheckoutService` drops it at the service boundary for that reason, and this
method does not get to be the softer door. A number therefore only lands when the order is
Takeaway, or when the operator has passed <paramref name="promoteToTakeaway"/> to make it so
deliberately. The alternative — letting the sheet write whatever it likes — would put the one
legally meaningful rule in a file that no test covers and that the next screen will copy.
**NO STATUS CHANGE AND NO HISTORY ROW.** The order does not move and no
`OrderStatusHistory` row is written: the status genuinely did not change, and a history that
claims otherwise is a worse record than no history. What DID change is personal data on a closed
sale, which is why <paramref name="promoteToTakeaway"/> has to be explicit.

## ShiftStats

```csharp
public sealed record ShiftStats(
```

These were declared at the foot of `IOrderService.cs`, behind an interface they had nothing to
do with — a file named after one type holding five others is the same mistake the interface split
was fixing, one level down. They live here now because they are the reporting contract's own types
and reading them is how you know what a shift figure means.

## int

```csharp
int PaymentsCount,
```

"Принято оплат": what the till actually recorded, split by method. Added at the end and
kept separate from Revenue on purpose — Revenue stays SUM(TotalKopecks) over completed
orders, so a shift's revenue never depends on payments having been recorded. The two agree
for completed orders (they cannot become Ready unpaid) and the payment total additionally
covers orders that were paid in advance and are still cooking.

## decimal

```csharp
decimal RefundsCash,
```

"Возвращено": money that left the till again, split by the method it left in. Appended at the
end and kept strictly additive on purpose — the four Payments* fields above stay GROSS, so
every existing reading of them ("what came in") is unchanged, and what is actually in the
drawer is PaymentsCash − RefundsCash. There is no refund count: nobody reconciles a count of
refunds, only money.

## decimal

```csharp
decimal FloatCash,
```

"Внесено размена" and "Изъято на инкассацию": money that moved through the drawer by hand,
which is to say without a customer behind it. Placed here, immediately before
ExpectedCashNow, because they are two of the four terms that figure is made of — ExpectedCashNow
is FloatCash + PaymentsCash − RefundsCash − PayoutCash and a reader who cannot see the other
three has to trust it.
Both are NET of correcting entries: a cancelled top-up lowers FloatCash rather than appearing
as a second, negative float. Reporting the gross and the correction separately was rejected —
an operator reconciling a drawer wants "how much change is in here", and two rows that net to
one number is an arithmetic step in the middle of a count.

## decimal

```csharp
decimal ExpectedCashNow,
```

The cash reconciliation, appended strictly last and additively like the Payments* block above.
ExpectedCashNow is the LIVE drawer figure — float plus cash from orders, less cash handed back
and less cash carried away — and it is a real field rather than something each screen subtracts
for itself: one definition, computed once where all four halves already exist. It MAY be
negative, because a cash refund taken after a collection leaves money the drawer no longer
holds; that is shown rather than prevented, and see CashLedger for the full case.
Reconciliation is what was physically counted at the close, with the ledger figure it was
compared against FROZEN into it, so the comparison survives every refund taken afterwards.
IsReconciliationStale is the single boolean that says the frozen expectation and the live
figure have parted company — a refund against a closed shift moves the live figure and leaves
the snapshot alone, because the money physically left that drawer. There is no second money
column and no "expected" without a moment attached.

## CashReconciliation

```csharp
public sealed record CashReconciliation(
```

The recorded cash count of one closed shift: what the ledger said the drawer held at the close
(<paramref name="ExpectedKopecks"/>, frozen and never moved afterwards), what was physically
counted, when it was RECORDED and, when the two differ, why.
The difference is DERIVED, not stored. A column for it would be a third copy of
`Counted − Expected`, and a third copy is a third thing to keep in step when one of the two
is written; there are four columns on the shift and none of them is the discrepancy.

## ProductAnalyticsRowData

```csharp
public sealed record ProductAnalyticsRowData(
```

One line of the shift's product breakdown.
<param name="ProductName">The name snapshot carried by the order line.</param>
<param name="ModifierName">The modifier/variant description, already composed by the query.</param>
<param name="Quantity">Units sold across the shift's completed orders.</param>
<param name="Revenue">Line revenue in major units.</param>
<param name="CategoryId">
The section the dish belongs to TODAY, or null when it has none. Read through the product, not
snapshotted onto the order line — see `IOrderReporting.GetProductAnalyticsAsync`.
</param>
<param name="CategoryName">That section's current name, or null.</param>

## DiscountedLine

```csharp
public sealed record DiscountedLine(
```

One order line whose charged price differs from the price it was allowed to be sold at, with the
size of the difference.
<param name="OrderNumber">Which order, in the form the operator reads on a receipt.</param>
<param name="Status">
Whether the order is a completed sale or was voided. Carried because the two must not be SUMMED
together: a voided line's money was refunded, so adding its discount to a real sale's would report
money that was never kept.
</param>
<param name="Quantity">Units on the line.</param>
<param name="ListPriceKopecks">
The allowed unit price, as the server states it: a bundle's own catalogue price, or the charged
price itself for a line with nothing to compare against.
</param>
<param name="PriceKopecks">The unit price actually charged.</param>
<param name="ReferenceTotalKopecks">
What the same dishes would have cost on their own, à la carte, for the WHOLE line — or null for a
line that is not a bundle. This is the second, independent signal: the first says "the price was
changed", this one says "this bundle was cheaper than its parts", and a bundle can be the second
without being the first. It is a NEGATIVE number for a bundle priced above its parts, which is a
surcharge and not a discount.
</param>

## BundleSavingKopecks

```csharp
public long? BundleSavingKopecks => ReferenceTotalKopecks - ChargedTotalKopecks;
```

How much less the bundle cost than its parts, for the whole line. Null for a line with no
composition, and deliberately a DIFFERENT figure from `DiscountKopecks`: a bundle
sold at its own price has nothing to report in the override column and may still be cheaper than
its parts. NEGATIVE when the bundle was dearer than its parts — a surcharge, which is legal and
must be labelled as one rather than as a negative discount.

## if

```csharp
if (!order.IsFullyPaid) throw new ConflictException("Заказ не оплачен: сначала примите оплату.");
```

Both transitions are gated, so this sits before the switch: nothing may leave the bar
unpaid, which is the only thing that makes "revenue counts completed orders" and "the
till received the money" the same statement.
WHY checking IsFullyPaid here is sound — and the condition that would break it:
UpdateOrderAsync refuses any order that is not InProgress, so the total is FROZEN the
moment an order becomes Ready. An order that was fully paid can therefore never become
underpaid, because the only writable total cannot change afterwards. A refund lowers
PaidKopecks, so it would break exactly this — which is why RefundAsync accepts only
Completed orders: by then the status cannot move and the guard can never be reached again
with a lowered scalar. "Paid" stays permanent for every order that is still travelling
through the bar, and a refund is something that happens to a sale that already left it.

## MarkSeenAsync

```csharp
public async Task MarkSeenAsync(Guid orderId, CancellationToken cancellationToken = default)
```

An explicit scalar update, not a tracked load and SaveChanges. This runs from a card tap on a
board that rebuilds itself every few seconds, and it touches exactly one column; loading the
entity would drag in every navigation and invite an accidental write to something else.
THE PREDICATE IS THE KEY AND NOTHING ELSE, and it is there because the first version was an
over-engineering that broke the feature. It read
`Id == orderId &amp;&amp; (SeenAt == null || SeenAt &lt; now)` — "not already seen" as a
database-side guard — and EF could not translate the disjunction over a nullable
`DateTimeOffset`, so `ExecuteUpdateAsync` threw
`InvalidOperationException: The LINQ expression could not be translated` on the FIRST tap.
The dot therefore never cleared: the write was attempted, failed, and was swallowed by the
caller's catch into a file log nobody reads. The guard was redundant in the first place —
`now` is later than any earlier `SeenAt`, so writing it unconditionally is ALREADY
monotonic, and the caller only calls this when the order is genuinely unseen. A guard that
cannot be translated is worse than no guard.

## if

```csharp
if (order.Status == OrderStatus.Cancelled)
```

A voided order is a sale that did not happen. Its total is gone and its money went back, so a
customer who "remembers a number" for it is describing a different order — and a phone on a
cancelled row would outlive the sale it describes.

## if

```csharp
if (wantsPhone && order.OrderType != OrderType.Takeaway && !promoteToTakeaway)
```

THE GATE, AND IT IS DELIBERATELY HERE RATHER THAN IN THE SHEET. Counter service may not hold a
number, so a phone on one is refused unless the operator has passed promoteToTakeaway — the
owner's decision that naming a number is itself the statement that the customer is taking the
order away. Making the UI decide this would mean the legal contour lives in a view, and the
next screen to write this column would not know about it.

## CustomerPhone

```csharp
order.CustomerPhone = ContactPhoneRule.ForStorage(phone, OrderType.Takeaway);
```

Validated as Takeaway in both branches, because that is the mode the number is being stored
under — including the branch where it already was one. ContactPhoneRule drops the value
without reading it for any other mode, so passing the real mode here would silently
discard a number the operator just typed.

## CancelOrderAsync

```csharp
public async Task CancelOrderAsync(
```

Voids a sale: the order leaves the revenue, the money goes back, and — only if the operator
says nothing was made — the stock write-off is undone. Everything lands in one transaction,
because a voided order with the cash still in the drawer (or the stock restored on an order
that is still counted) is worse than either outcome on its own.

## transaction

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
```

Same shape as CheckoutService: the refund rows, the stock movements and the status change
are one fact about the till, and a partial write of it is the definition of books that do
not close. StockPlanner.ReverseAsync can also refuse (an order-scoped receipt in the
journal), and the transaction is what makes that refusal leave nothing behind.

## refundedKopecks

```csharp
var refundedKopecks = order.PaidKopecks;
```

A paid order CAN be cancelled — it used to be refused, which was the only thing keeping
the sale in the books while the cash stayed in the drawer. Cancelling now takes the money
with it: the refund mirrors the original payment methods, so the drawer loses exactly what
it took and the terminal is credited nothing it never paid out.

## ComposeCancellationReason

```csharp
private static string ComposeCancellationReason(
```

Orders.CancellationReason and OrderStatusHistory.Comment are both 300 characters
(AppDbContext.Catalog), so the composition is built once and truncated at the single place
that knows the limit. Truncating at the end is deliberate: the money and the stock
disposition are facts about the till, and the operator's free text is the only part that can
afford to lose its tail.

## names

```csharp
var names = string.Join(", ", irreversible);
```

Names when the catalogue still has them, identifiers when it does not. Either way
the operator is told the return was partial: silently skipping would let the shelf
and the books disagree with nothing on screen to show it.

## UpdateOrderAsync

```csharp
public async Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default)
```

Differential update: existing lines keep their identifiers, only quantities are updated,
missing lines are removed and new lines are inserted. The old implementation deleted and
recreated every line on each save, destroying line identity needed for audit/printing.
Lines are matched by `OrderLineKey` — product, modifier, variant AND the component
signature — rather than by the three-field comparison that was written inline here. That
comparison had already diverged from the one in the cart: it left the variant out of nothing at
all but it also could not tell two bundles apart, so two different builds of the same bundle
merged into one line at whichever price was added first. The whole reason the key moved into the
core is that one function every caller computes the same way cannot drift apart again.

## resulting

```csharp
var resulting = new List<OrderItem>(items.Count);
```

The reconciled line set, kept separately from order.Items: a new line is tracked through
the DbSet (an entity pushed into the Items collection of a tracked order is tracked as
Modified, and EF then updates a row that does not exist yet), and EF only links it into
the loaded collection during DetectChanges.

## existing

```csharp
var existing = resulting.FirstOrDefault(Matches) ?? order.Items.FirstOrDefault(Matches);
```

BOTH LISTS, AND THE ORDER IS THE POINT — this lookup was the site of two defects in a row, and
both are the same mistake about which lines exist. A line added earlier in THIS call is
not in order.Items yet, so two identical lines in one save — the ordinary "the same
bundle twice in one order" case — could not find each other and were inserted as two
rows of quantity 1. Searching `resulting` alone fixes that and breaks the rest: the first
line of the call finds nothing there and every existing line gets rewritten as a new one.

## price

```csharp
var price = OrderLinePricing.Resolve(
```

A line ADDED to an open order gets its allowed price decided here, exactly as at
checkout, so the shift report's discount section cannot be walked around by editing
the order instead of selling it. For a bundle that allowed price is the bundle's OWN
price, read from the catalogue — the same number checkout would have used, from the
same loader, so the two cannot disagree about what a bundle may be sold for.

## SyncComponents

```csharp
SyncComponents(db, existing, incoming);
```

ListPriceKopecks is deliberately NOT rewritten here. It is the price the line was
ALLOWED to be sold at, written once when the line was created, and an edit that
recomputed it would erase the very signal this method exists to preserve: re-pricing
a line during the sale would become invisible, because the new charged price would be
written straight back into the allowed price and the report would have nothing left
to compare. A manual review is allowed to show up as a discount, not to be laundered
into "this is what it should have cost".

## ResolveBundlePriceAsync

```csharp
private async Task<long?> ResolveBundlePriceAsync(OrderItem incoming, CancellationToken cancellationToken)
```

The bundle's own price from the catalogue, or `null` when the line is not a live bundle.
ONE read per added line, and only for a line that has a composition at all. That is the price
lookup rather than the sale-side resolver, deliberately: an order editor saves whatever the
order holds, and a line whose template has been deleted since the sale has no price to be
refused over — the answer is "the catalogue has no price for this", and the comparison falls
back to the à la carte sum inside `OrderLinePricing.Resolve`.

## ToComponentSignature

```csharp
private static IEnumerable<(Guid productId, int quantity)> ToComponentSignature(OrderItem item) =>
```

The (dish, count per unit) pairs a line's composition folds into its merge key. An empty
composition gives an empty signature, which is what makes an ordinary dish and a bundle of it
two different lines rather than one line at the bundle's price.

## ToCheckoutComponents

```csharp
private static IReadOnlyList<CheckoutComponent> ToCheckoutComponents(OrderItem item) =>
```

The composition in the shape `OrderLinePricing` and `ComboPricing` read.
Only the identifier, the multiplicity and the slot price matter — the last one for the à la carte
reference, which is the fallback when the catalogue has no price for the line; the names and the
reference prices come back from the persisted rows when the line is saved.

## WriteComponents

```csharp
private static void WriteComponents(AppDbContext db, OrderItem item, OrderItem incoming)
```

The composition snapshot for a line, written through the DbSet.
A snapshot is rewritten wholesale here, unlike the catalogue side which matches by identity:
this is a record of what one sale was, and the caller has just stated what that sale is now.
Keeping rows that were not mentioned would leave a component on the receipt that is no longer
being sold — the composition would describe two different sales at once.
The DbSet Add is the ONLY link, and the item's own `Components` collection is deliberately
NOT appended to by hand. Doing both is a trap worth spelling out, because it is the mirror image
of the one written out at `DraftOrderService`: adding a row whose foreign key points at an
ALREADY TRACKED parent makes EF fix up the inverse navigation and append the entity to
`item.Components` by itself, so pushing the same instance in again leaves the collection
holding every snapshot twice.
That was measured here rather than reasoned about: two slots came back as four, and the
consequence is far worse than a wrong count on screen — the doubled composition is what the
merge key is built from, so two halves of one bundle stopped matching each other and every
identical pair of lines became two rows. The receipt would have printed the composition twice
over; the till would have sold two of everything.

## Remove

```csharp
db.OrderItemComponents.Remove(stale);
```

Removed through the DbSet as well: the rows are tracked entities of the loaded
composition, and detaching them from the collection alone would leave them in the change
tracker as unchanged — the receipt would keep printing a slot that is no longer sold.

## return

```csharp
return await db.Orders
```

ThenInclude, and not on purpose-later: OrderItem.Components is an un-Included collection that
reads as EMPTY rather than stale — see the remarks on Order.Payments. An order read back for
the details screen therefore has to ask for its composition explicitly, or a bundle prints as
a single line with nothing under it. The order LISTS below do not include it, because a list
of order lines has no use for the composition and would pay for a join on every row.

## orders

```csharp
var orders = await db.Orders.AsNoTracking()
```

Completed + Cancelled, i.e. everything that is closed. The status is left on the entity
on purpose: the history list renders it, so a voided sale shows up AS voided instead of
being absent from the only list a manager reads after the fact.

## OpenShiftAsync

```csharp
public async Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default)
```

The float and the shift go in together, deliberately: a shift that exists before anyone has
said what was in the drawer is a shift whose end-of-shift count has nothing to be compared
against. That is precisely how this feature was broken before — the shift appeared by itself
on first sale, so there was never a moment at which the change in the till was recorded
against a shift that did not yet have orders in it.
The overlap guard runs first and before the float is validated, for the same reason the close
checks its open orders first: the operator needs to know which part of the flow is wrong, and
"a shift is already open" is a different fix from "that amount is impossible".

## Add

```csharp
db.CashMovements.Add(new CashMovement
```

The float is a CashMovement like any other, so the opening change is read by the same
arithmetic as a mid-shift top-up and shows in the same list on screen. It is NOT a column
on the shift: a column would make "the float" a different kind of fact from "change added
at 11:40", and the drawer is one sum of the same rows.

## counted

```csharp
var counted = await db.Shifts.AsNoTracking()
```

Ordered IN MEMORY, and that is not laziness: SQLite cannot ORDER BY a DateTimeOffset, so
this throws NotSupportedException rather than sorting silently. A terminal has one shift
per working period, so the rows are a handful and loading two scalars each is cheaper than
any workaround that would keep the ordering in SQL.

## CloseShiftAsync

```csharp
public async Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default)
```

Closes the active shift and opens a new one, recording the counted cash against the drawer
figure the ledger holds at that moment. Refuses to close a shift that still has orders in
progress — previously a shift could be closed while orders were cooking.
The refusal also names how much has not been collected, count and kopecks. A manager must
not close a shift while 3000 ₽ of uncollected cash is still open on the bar, and a bare
"3 orders left" gives no reason to go and take the money first.
Reconciling is MANDATORY: the shift does not close without a count, and a difference has to
carry a reason. A count of 0 is a real count (an empty drawer is missing money, not an absent
count), and a count is never negative.
THE FOUR COLUMNS ARE WRITTEN ONCE. There is no "исправить пересчёт" path and there must not
be one: a count that can be silently overwritten is worse than no count, because the audit
trail then says something the operator never stated. Recounting, if it is ever wanted,
needs an append-only `CashCounts(ShiftId, CountedAt, CashKopecks, Reason)` table and a
report that reads both — not an UPDATE over this row.
What this does NOT freeze: a refund taken against an ALREADY CLOSED shift is allowed and
moves that shift's LIVE drawer figure, because the money physically left the drawer that
shift owned. The snapshot stays exactly where it was, and
`ShiftStats.IsReconciliationStale` is the single boolean that reports that the
two have parted company. There is no second money column, and the snapshot is never
relabelled "expected" without the moment it was taken at.

## openOrders

```csharp
var openOrders = await db.Orders.AsNoTracking()
```

Only the two money columns come back: SQLite cannot SUM an expression, so the open
orders' totals are projected and the difference folded in memory. The set is a handful
of rows (everything else is closed), and a closed shift cannot have orders left open.
DECISION, stated because it looks like an oversight otherwise: neither cancellation nor
refunding can block the close, and that is deliberate on both counts. A voided order is
Cancelled and a refunded one is Completed, so neither is in the InProgress/Ready set
below; a fully refunded Completed order has PaidKopecks == 0 and is exactly the order a
manager must be able to close out. Nothing here needs a status filter widened or a
"was it paid" test relaxed for the refund feature.
The consequence, which is a decision rather than a bug: a refund against an ALREADY CLOSED
shift is allowed and retroactively changes that shift's report, because
GetShiftStatsAsync recomputes live off the ledger with no cache. That is correct — the
money physically left the drawer that the shift owned, so it belongs to that shift's
report whether the operator pressed the button during it or the next morning. Booking it
into the new shift instead would be the lie: the new shift's drawer never held the cash.
ORDERING IS LOAD-BEARING: this guard runs BEFORE anything about the count or the reason.
The operator needs to know which part of the flow is wrong first — the bar still has orders
on it — and a close attempted with an empty drawer against a full expectation must report
the open orders, not the missing reason. Reordering the two would make an unrelated test
fail with the wrong exception type.

## if

```csharp
if (active is null)
```

No active shift means there is nothing to reconcile the count against. Refusing beats the
alternative this used to have — open a fresh shift and drop the count on the floor — since
the startup bootstrap and GetOrCreateActiveShiftAsync both guarantee an active shift, so
this is only reachable through a hand-edited database, and silently discarding a cash
count is the one outcome money must never have.
This is reachable through the UI only by a terminal whose shift was closed and never
reopened; it used to be defended by an invariant that no longer exists, because startup no
longer creates a shift. The refusal stays: a count with nothing to reconcile against would
have to be discarded, and discarding a cash count is the one outcome money must never have.

## ledger

```csharp
var ledger = await CashLedger.ReadAsync(db, active.Id, cancellationToken);
```

The expectation, frozen by being read HERE and written to the row below.
CashLedger, NOT ShiftPayments: the drawer is the float plus the cash from orders minus the
cash handed back minus what was carried away, and a count taken against the payments alone
would report a shortage of exactly the change that was in the till all day. Read once, in
this context, so the figure written is the figure that was compared against.
It CAN be negative here, and that is not a bug to guard against: a cash refund taken after
a collection leaves money the drawer no longer holds (see CashLedger for the full case).
The count below cannot be negative, so such a shift closes as an overage with a reason
demanded, which is the correct thing to make a human look at.

## if

```csharp
if (countedCashKopecks < 0)
```

0 is a REAL count and is stored as one. An empty drawer against a non-zero expectation is
the single most important case this feature has to catch, so the guard is `>= 0` and
nothing else: a `?? 0`, a HasValue check or a `<= 0` here would swallow exactly that value
and turn missing money into an uncounted shift.

## if

```csharp
if (discrepancyKopecks != 0 && reason is null)
```

Required iff the count does not match, and never required when it does: demanding a reason
for a drawer that came out exact would train the operator to type filler into an audit
field. A reason on a match is accepted and stored.

## if

```csharp
if (reason is { Length: > MaxDiscrepancyReasonLength })
```

DELIBERATE INCONSISTENCY with PaymentRecorder.TruncateNote, which silently shortens a
refund reason instead. That one may be cut because the refunded money and the order it
belongs to are already recorded; this one may not, because this string is the ONLY record
of why the drawer did not balance, and DisplayPromptAsync has no MaxLength to stop a
paste. Rewriting it to 300 characters would alter what the operator stated and still look
like a complete answer. So: refuse, and leave the shift open.

## ReconciledAt

```csharp
active.ReconciledAt = now;
```

"Recorded at", not "counted at": the operator counted the drawer a couple of minutes before
this save, so the value equals EndTime to the second. Keeping the column is worth that
small lie — it is the audit moment, and it is what makes a recount cheap to add later —
but nothing derives anything from it (see Shift.ReconciledAt).

## SaveChangesAsync

```csharp
await db.SaveChangesAsync(cancellationToken);
```

NO NEXT SHIFT IS CREATED HERE, and that is the change this method exists to make. It used
to open one, because a shift had to exist before anybody could sell anything. Now a shift
is opened on purpose with the change recorded against it, so closing leaves the terminal
with NO open shift — which is the state the opening screen is for, and the state in which
the operator is asked to count the change they are about to put in.
The old behaviour had a second, quieter cost: the new shift opened with a drawer figure of
zero while the physical drawer still held yesterday's change. Every report for the rest of
that shift understated it by exactly that amount.
ONE save for the whole close: the four columns and the end of the shift are a single event,
and a half-applied close would leave a shift that is neither open nor reconciled.

## purgedPhones

```csharp
var purgedPhones = await contacts.PurgeAsync(ContactDataService.DefaultRetention, cancellationToken);
```

Retention runs here, AFTER the close has been committed, and that ordering is the decision.
A sweep is housekeeping; the cash count is the audit record of what was physically in the
drawer. If the sweep threw, the close must still be complete — it already is, and the next
close sweeps again. Making the purge a precondition of closing a shift would let a
housekeeping query cost a manager the ability to end their working day, which is a bad trade
whichever way the failure went.

## OrderService

```csharp
public sealed partial class OrderService
```

Taking money back after the fact. The refund itself — how much, against which source payments and
how the scalar moves — belongs to `PaymentRecorder`, the single place where the
order's net cash moves; this file is the policy around it.

## RefundAsync

```csharp
public async Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default)
```

Refunds part or all of a finished order. <paramref name="amount"/> is rubles, clamped to what
the order still holds, and is mirrored back out of the methods the money originally arrived
in — there is no payment-method argument, because the drawer and the terminal are two real
tills and a refund has to leave the one that was credited. An order stays
`OrderStatus.Completed`: a partial refund returns part of the money for a sale
that did happen, and voiding the whole sale is what cancelling is for.

## Refund

```csharp
PaymentRecorder.Refund(db, order, Money.ToKopecks(amount), reason, timeProvider.GetUtcNow(), logger);
```

Restricting refunds to Completed is load-bearing, not a UX preference: it is what keeps
AdvanceStatusAsync's IsFullyPaid guard sound. That guard relies on an order's total being
FROZEN from the moment it becomes Ready (UpdateOrderAsync refuses anything that is not
InProgress), so a paid order can never become underpaid — and a refund lowers
PaidKopecks, so allowing it before the goods leave the bar would make a Ready order
underpaid with no way back. CloseShiftAsync's open-order money math is untouched for the
same reason.

## OrderService

```csharp
public sealed partial class OrderService
```

All reporting aggregates in one place. Revenue/counts are computed in SQL over the integer
kopeck columns; only the timestamp based metrics (peak hour, average durations) are folded
in memory because SQLite cannot translate localization-aware date math.

## ledger

```csharp
var ledger = await CashLedger.ReadAsync(db, shiftId, cancellationToken);
```

"Принято оплат" is a separate aggregate from Revenue on purpose. Revenue stays
SUM(TotalKopecks) over completed orders, so a day's revenue never depends on payments
having been recorded; this is what the till actually took, split by method for the cash
count. The two coincide for completed orders (they cannot become Ready unpaid) and the
payment total additionally covers orders paid in advance while still cooking.
Both halves stay GROSS and separate: the four Payments* fields keep meaning "what came
in", unchanged and in this order, and the refunds are additive on top. The number a
manager physically counts is PaymentsCash − RefundsCash, and it is named ONCE, here, as
ShiftStats.ExpectedCashNow — not computed at each point of presentation, because a screen
that subtracts the two halves itself is a second definition of the same figure, and a
second definition is how two reports end up disagreeing about the drawer.

## shift

```csharp
var shift = await db.Shifts.AsNoTracking()
```

The frozen count comes off the shift row itself. AsNoTracking on purpose: this is a
report, and nothing here may accidentally mark a shift modified. A shift that was never
counted yields null — which is a fact of its own (every shift that predates the feature is
in that state) and NOT the same thing as a counted zero.

## isStale

```csharp
var isStale = ShiftReconciliation.IsStale(reconciliation, ledger.InDrawerKopecks);
```

Staleness lives here because this is the one place both numbers exist. A refund taken
against a closed shift moves the live figure and leaves the frozen snapshot alone, so the
two disagree from that moment on — and that disagreement is exactly what a manager has to
be shown, rather than a snapshot quietly rewritten to match the new reality.

## GetProductAnalyticsAsync

```csharp
public async Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default)
```

The section of each dish comes from `Products` → `Categories`, and that link must be
OUTER. `OrderItem` stores no category, so the join is the only source; a line whose
product row cannot be reached, or whose product has no section, is still a sale and still
belongs in the report.
**THE JOIN THAT DELETED SALES.** `Join(...)` with a cast-to-nullable key was believed
to give a LEFT JOIN. It does not — EF Core kept it INNER, so every line whose product had
`CategoryId == null` vanished from the breakdown while still counting in
`ItemsCount` and in revenue. Measured on the emulator: a shift that sold 30 items showed
14, the missing 16 being exactly the unfiled dishes, with nothing on screen to say so. The
symptom is the worst kind — every figure on the page looks reasonable, and the one number
that cannot be checked against anything else is quietly short.
The fix is `SelectMany(..., DefaultIfEmpty())`, which EF translates to a real LEFT JOIN.
Note what is NOT the fix: `GroupJoin` alone, and `GroupJoin` over a subquery with
the key already cast. Both compiled, both looked like an outer join, and both had to be thrown
away — the first because `DefaultIfEmpty` never reached the translator, the second
because EF emitted a correlated `APPLY`, which SQLite refuses outright.
The section is the dish's CURRENT one, not the one it had when it was sold. That is a
deliberate trade and it is not the same trade `Models.OrderItem.ProductName`
makes: the name is snapshotted because a rename must not rewrite what a customer was charged,
whereas a manager grouping yesterday's sales by where a dish sits TODAY is what "which
section is this performing in" means. A dish moved between sections regroups its history, and
the alternative — a snapshot column — would have needed a migration and would still be wrong
for the question actually being asked.
Grouping happens AFTER the join and carries the section, so a dish name that occurs in two
sections stays two rows and an unfiled dish needs no special case in SQL. There is
deliberately NO `ORDER BY`: ordering lives in `ProductAnalyticsProjection`
so a screen which forgets to sort cannot be left holding a stale order from SQL.

## completed

```csharp
var completed = db.Orders
```

ONE genuine inner join: a line whose order is not a completed order of THIS shift is not
this shift's sale, and that is the scope of the whole report.
The product lookup below is SelectMany(..., DefaultIfEmpty()) — a LEFT JOIN EF actually
emits. It is NOT written as Join with nullable cast keys: that form compiled, looked right
and was silently INNER, which deleted every unfiled dish from the breakdown while it still
counted in ItemsCount. Measured on the emulator: a shift that sold 30 items showed 14, and
the cards at the top of the page — which read a different query — said 30 the whole time.

## GetDiscountedLinesAsync

```csharp
public async Task<List<DiscountedLine>> GetDiscountedLinesAsync(Guid shiftId, CancellationToken cancellationToken = default)
```

**THE READ SIDE OF THE ONE CONTROL.** `OrderLinePricing` decides whether a price
was overridden at the moment it is written; this is where anyone finds out. No standard asks
for it — PCI DSS v4.0.1 has no occurrence of "discount" in 397 pages — and the argument is
margin: a median restaurant runs 2.8% net, so a 1% discount on sales eats roughly 36% of it,
while what the vendors ship starts at "the cashier may discount to 100%" (Lightspeed) or "Any
User" (Toast). The recomputation that makes it work — "the components do not add up to the
price" — is our own idea and not published practice; the published analogue is the before/after
review at Oracle, Bank of America and Bitta. What it buys is the FIRST occurrence caught with no
historical threshold to calibrate.
No reason code and no operator identity, both deliberately absent. A reason typed at the till
becomes the first value anybody ever picks and only the aggregate analysis of the pattern is
worth anything; and "who did it" needs staff entities, sign-in, PIN and permissions — a feature
the size of this one. The report says what was given away, and stops there.
The comparison itself is done in SQL because both figures are integer columns on the same row
and there is nothing to fetch first — the whole section is one query, not a scan of the shift's
orders with arithmetic folded in memory. The reference total is the exception: it is a SUM over
a CHILD table per line, which SQLite cannot do in a correlated subquery here, so the
compositions of the matching lines are loaded and folded here.
**WHAT THIS SECTION DELIBERATELY DOES NOT SHOW, SO IT IS NOT REDISCOVERED AS A BUG.** A
bundle that is sold at exactly its own price but deliberately cheaper than its parts does not
appear here: the filter is on the price mismatch, which is the question this section answers.
Surfacing those would be a SECOND section — "наборы выгоднее своих частей" — and a bundle
priced at a loss on purpose would otherwise drown the override report. It is a real thing a
manager may want, and it is deliberately not what this query returns.

## itemIds

```csharp
var itemIds = lines.Select(line => line.Id).ToList();
```

Σ(ReferencePriceKopecks × QuantityPerUnit) per line, then × the line quantity: what the same
dishes would have cost on their own. Nullable all the way through, because a line with no
components has no "cheaper than its parts" figure and null is a different statement from 0 —
0 would say the bundle cost exactly as much as its parts, which is a fact about a bundle and
not about an ordinary dish.

## 00

```csharp
return $"{group.Key:00}:00–{(group.Key + 1) % 24:00}:00 ({count} {TextFormat.Plural(count, "заказ", "заказа", "заказов")})";
```

TextFormat.Plural, NOT a bare "заказов". On device this read «11:00–12:00 (1 заказов)»
for a single-order shift, and the green test run said nothing because the rule is a
formatting concern, not an aggregate. The helper exists precisely for this — its own
docblock records that "1 товаров" was written in two files and corrected in one — and
every count around this one on the same page already pluralises correctly, which is
exactly why the single wrong form survived a visual pass on a 411dp phone.
