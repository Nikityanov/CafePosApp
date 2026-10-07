# Оформление и оплата

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## ResolvedLinePrice

```csharp
public sealed record ResolvedLinePrice(long ListPriceKopecks, long PriceKopecks, bool IsOverridden);
```

What a line was allowed to cost, what was actually charged, and whether the two disagree.
<param name="ListPriceKopecks">
The allowed price, as the SERVER states it: a bundle's own `PriceKopecks`, and the charged
price itself for a line with nothing to compare against. Immutable for the life of the line.
</param>
<param name="PriceKopecks">What is actually charged — what `Order.RecalculateTotal` sums.</param>
<param name="IsOverridden">Whether the two differ. The one bit this whole feature exists to produce.</param>

## OrderLinePricing

```csharp
public static class OrderLinePricing
```

**WHY ONE METHOD AND NOT ONE PER CALL SITE.** A Square user documented the bypass that shaped
this: a price check that lives on one branch is not a control at all, because a second branch that
writes the price never goes through it. Lightspeed ships a cashier with the power to discount to
100%, Toast with "Any User", and Square has a published route where the PIN is simply not asked.
So the check is placed where EVERY price passes, and both callers — `CheckoutService`
and `OrderService` — come through this method. A second implementation of
`Price != ListPrice` would be a place where the control silently does not apply, which is the
exact failure the control exists to prevent.
**WHY THIS IS NOT A STANDARD, AND IS STILL WORTH HAVING.** PCI DSS v4.0.1 (397 pages) contains
zero occurrences of "discount", "price" or "pricing": no standard requires this. The argument is
margin — a median restaurant runs 2.8% net, and a 1% discount on sales eats roughly 36% of that —
and the fact that this state (any price, no PIN, no trace) sits below what the vendors ship. The
recomputation itself ("the components do not add up to the price") is our engineering idea and is
not published practice; the published analogue is the before/after review at Oracle, Bank of
America and Bitta. What makes it useful here is that it catches the FIRST occurrence with no
historical threshold to calibrate.
**WHY IT IS DETECTION AND NOT PREVENTION.** The charged price is not overwritten: a manual
review at the till is a legitimate thing for an operator to do, and a POS that refuses it is a POS
that trains people to work around it. So nothing is blocked and nothing is asked for at the till —
the manager sees the divergence in the shift report, after the fact, which is Tier-1 detection with
no friction at the counter. That is why there is no reason code either: a reason typed at the till
becomes the first value anyone ever picks, and only the aggregate analysis of the pattern is worth
anything. Who did it is deliberately absent too — that needs staff entities, sign-in, PIN and
permissions, a feature the size of this one, and the report says so rather than guessing.
**THE COMPARISON IS ONLY WORTH ANYTHING IF THE COMPONENTS ARE THE SERVER'S.** A client that
chose its own component list could always make the two agree. That is why the caller passes
components resolved against the catalogue (see `IComboService.ResolveSaleComponentsAsync`) and
never the ones the client sent: a fake composition cannot pretend to be the computed one, because
the computed one is not the client's to choose.

## Resolve

```csharp
public static ResolvedLinePrice Resolve(
```

The allowed price of one line, and the divergence from what is charged.
<param name="components">
The line's composition as resolved against the catalogue — empty or `null` for an
ordinary dish.
</param>
<param name="chargedKopecks">The unit price actually charged, in kopecks.</param>
<param name="bundlePriceKopecks">
The bundle's OWN price, read from the catalogue by the caller
(`IComboService.ResolveSaleCompositionsAsync` / `ResolveSalePricesAsync`). This is
what the till was allowed to charge, and it is deliberately NOT computed here from the
components: see the remarks.
</param>

A line with no components has nothing to compare against, so its allowed price IS the charged
price and it can never be reported as overridden. That is not a gap in the control: before
bundles existed there was no second figure to compare, which is exactly why the column had to
be backfilled as `ListPriceKopecks = PriceKopecks` — otherwise every historical line would
show as a 100% discount. What a plain line can still be caught for is a LATER price edit, and
that works because the allowed price is written once when the line is created and never
rewritten afterwards.
**THE ALLOWED PRICE OF A BUNDLE IS THE BUNDLE'S OWN PRICE, NOT THE SUM OF ITS PARTS.** The
sum is the à la carte reference and it is a different question, reported by the second signal
`Σ(ReferencePriceKopecks × QuantityPerUnit)`. Recomputing the allowed price from the
components would be the old model in one line: a bundle priced above its parts would be reported
as a discount, a manual review of a correctly priced bundle would be reported as a discount
against a number nobody allowed, and a dearer substitute would move the price again — the three
exact things the reversal exists to stop.
**THE ONE FALLBACK, AND WHY IT IS NOT A BUNDLE PRICE.** When a line has a composition but
the catalogue has no price for it — a hand-built composition whose template is gone, edited
onto an open order — the à la carte sum is used, because it is the only statement about such a
line the SERVER can make. Dropping to the charged price instead would disable the control on
exactly the path where a caller fabricates a composition most easily, and the shift report
would show nothing. It is weaker by construction and the doc on
`OrderItem.ListPriceKopecks` says so; it is not a second comparison, it is this one, in
the one method every price passes through.

## PaymentBooking

```csharp
public static class PaymentBooking
```

**THESE TWO ANSWERS MUST NOT DISAGREE, AND NOTHING ENFORCED THAT.** The checkout path had two
independent ternaries over the same flag: whether the order is written with a payment row, and
whether the operator is then told «оплачено картой» or «оплата не получена». Change one and not the
other and you get an order that says it was paid with no payment behind it — or the reverse, an
unpaid order announced as paid. Both are silent, and one is a till that stops balancing.
The rule they encode: a deferred payment books the order with NO `PaymentIntent` at
all, the same overload without one, so the two paths write an identical order and differ only in
whether a payment row exists. Nothing is clamped, defaulted or invented — deferred means the
operator declined to take money, and the only honest record of that is its absence.

## ReceiptText

```csharp
public static string ReceiptText(PaymentMethod method, bool isDeferred) =>
```

Derived from the SAME flag as `IntentFor`, and deliberately phrased so neither
reading can pass for the other: «оплата не получена» says the money has not arrived, which is
not the same claim as «оплата отложена» — one is a fact about now, the other a promise about
later, and the first is the one the operator needs.

## Describe

```csharp
public static string Describe(StockShortage shortage) =>
```

**BOTH FIGURES, NOT JUST THE VERDICT.** «Недостаточно молока» tells the cashier the sale is
refused and nothing about what to do; «нужно 0,5 л, есть 0,2 л» tells them whether to make the
latte, substitute, or offer something else.
The quantities go through `TextFormat.Quantity` rather than a format string written
here. That is the app's existing convention for a stock amount — trailing zeros dropped, three
decimals kept so 0,25 is not rounded away — and a second formatting rule for the same kind of
figure is a second thing to keep in step. The decimal separator follows the device's culture,
as it does everywhere else in the app.

## customerPhone

```csharp
var customerPhone = ResolveCustomerPhone(details);
```

The phone is validated BEFORE the loop and before any database work, so a typo in a phone
number cannot consume an order number or leave a half-written order behind. It is validated
rather than silently dropped because a takeaway customer who gave a number wants to be
reachable, and a number nobody can dial is worse than no number at all — the kitchen would
try to call it.

## ResolveCustomerPhone

```csharp
private static string? ResolveCustomerPhone(OrderDetailsIntent details) =>
```

The phone that will actually be stored, or `null`.
**COUNTER SERVICE STORES NONE, AND THAT IS THE POINT OF THE WHOLE FEATURE.** 152-ФЗ
ст. 6(1)(5) permits a phone to be processed only where it is needed to perform the contract, and
ст. 5(7) requires erasing it once that purpose is met; a customer eating on the premises needs
to be found in no way at all, so the value is dropped HERE, at the boundary, instead of being
written and then deleted later. EDPB Guidelines 2/2019 п. 25 is the same argument from the other
side: where a less intrusive option exists, the processing is unnecessary. CoAP 13.11 ч.12
prices unlawful disclosure at 3–5 million ₽ for 1 000–10 000 subjects and counts ROWS, so what
matters is how many rows exist, not whether the column does.
The three decisions themselves live in `ContactPhoneRule`, not here. That is not
tidiness: this method used to be the only place the rule existed, which meant the second door into
the same column — a phone added to an order AFTER it was paid for — had to copy it. A copy of a
legal contour is exactly the kind of thing nobody writes a test for and that quietly diverges. The
reasoning stays here; the mechanism does not.

## resolvedCompositions

```csharp
var resolvedCompositions = await combos.ResolveSaleCompositionsAsync(lines, cancellationToken);
```

The composition and the price the SERVER is willing to sell, rebuilt from the catalogue.
Everything from here on uses these rows and not the ones the client sent: the names on the
receipt, the ingredients written off and the price the control compares against all have to
be the catalogue's figures, or a forged cart decides them.

## demands

```csharp
var demands = ComboExpander.Expand(sellable);
```

Validate stock before anything is written, inside the same transaction. The demands are the
EXPANDED ones, so a bundle's slots reach the recipe lookup instead of the bundle line (which
has no recipe of its own) and its ingredients are written off for the first time.

## price

```csharp
var price = OrderLinePricing.Resolve(composition.Components, Money.ToKopecks(line.Price), composition.PriceKopecks);
```

THE ONE PRICE CHECK. The allowed price is the bundle's OWN price, read from the
catalogue — not a sum of its parts, and not anything the client asked for. The charged
price stays what the caller sent, because a manual review at the till is allowed: it is
the divergence that gets reported afterwards, not something the till has to argue about.
For an ordinary dish the allowed price is the charged one, as it always was.

## ReplaceComponents

```csharp
private static IReadOnlyList<CheckoutLine> ReplaceComponents(
```

The same lines with their composition replaced by the resolved one. `with` on a record, so
the line keeps its identity, price and quantity — only the slots are swapped.
The bundle's own price does NOT travel back into the line either: it is the allowed side of the
comparison in `OrderLinePricing.Resolve`, and a line's `Price` is what the
customer was charged, including a manual review the till is allowed to make.

## WriteComponents

```csharp
private static void WriteComponents(AppDbContext db, OrderItem item, IReadOnlyList<CheckoutComponent> components)
```

Writes the composition snapshot onto the line.
`OrderItemComponent.ProductId` is a plain value with no foreign key, so a line outlives the
catalogue entry that produced it — which is what lets an old receipt be reprinted unchanged and
lets a deleted dish not take a historical order with it.
Through the DbSet, and NOT pushed onto `item.Components` by hand. This is the trap written
out at `DraftOrderService` and `CatalogService.SyncVariants` — an entity that was never
Added is tracked as Modified against an unchanged parent and EF then UPDATEs a row that does not
exist — plus its mirror image, which cost a day of confusion: once the parent IS tracked, the
Add fixes up the inverse navigation by itself, so pushing the same instance in again leaves the
collection holding every snapshot twice. The order handed back to the caller still carries its
composition, because the fixup is what links it.

## WarnAboutMissingRecipesAsync

```csharp
private async Task WarnAboutMissingRecipesAsync(AppDbContext db, IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken)
```

Warns about bundle components that have no recipe, and sells anyway.
A warning, never a refusal: a missing recipe row is a catalogue omission, and blocking the sale
would turn it into a till that cannot sell coffee until somebody fixes the catalogue. But
SILENCE is the actual damage — the ingredients leave the shelf without being written off, and
the stock quietly drifts away from the books with nothing on screen to show it. Toast keeps an
86 report for the same reason: the sale happens, and the missing link is visible.
Read on the SAME context and inside the same transaction as the shortage check, for one reason:
a second connection reading while this transaction is open is a lock the phone's flash does not
need to take, and the answer cannot differ between two connections anyway — the catalogue is not
being written by the sale.

## RequireOpenShiftAsync

```csharp
private async Task<Shift> RequireOpenShiftAsync(AppDbContext db, CancellationToken cancellationToken)
```

This used to CREATE the shift, so that selling could never fail for want of one. That is now
a refusal, and the change is the point of the opening screen: a shift created by the first
sale is a shift whose opening change nobody ever recorded, and the end-of-shift count is then
compared against a drawer that was never declared to hold anything.
The refusal is thrown from inside the checkout transaction and before stock is validated, so the
transaction is unwound untouched and no partial order is left behind.

## CheckoutComponent

```csharp
public sealed record CheckoutComponent(
```

A snapshot, not a reference. The checkout writes these onto
`Models.OrderItemComponent` unchanged, so the name on a reprint is the name that was
on the receipt and the reference price is the one the discount was measured against.
Kopecks, unlike `CheckoutLine.Price` which speaks rubles: these two figures are written
straight into a line and compared against each other to decide whether a bundle was cheaper than
its parts, and a ruble round trip in the middle of that comparison would be a rounding step whose
only possible effect is to make a bundle look discounted when it is not.

## Components

```csharp
IReadOnlyList<CheckoutComponent>? Components = null);
```

The slots of a bundle, empty or `null` for an ordinary dish. A bundle is still ONE line
with a quantity: N means N complete bundles.
These slots are NOT the price. `Price` is the amount charged, and for a bundle it
is expected to be the bundle's own price (`Combo.PriceKopecks`); the sum of the slots is
only the à la carte reference (`ComboPricing.ReferenceKopecks`), which the server
recomputes and which never reaches the customer. The server rebuilds both figures from the
catalogue regardless of what the cart sent — see
`IComboService.ResolveSaleCompositionsAsync`.

## PaymentIntent

```csharp
public sealed record PaymentIntent(decimal Amount, PaymentMethod Method);
```

What the customer hands over at the counter. `Amount` is rubles — the service/DTO
boundary speaks rubles and the entity speaks kopecks — and it is clamped to the order total
inside the checkout transaction: for a 1000 ₽ note on a 660 ₽ order the recorded payment is
660 ₽ and the 340 ₽ change is the UI's business. There is no till/petty-cash model that could
absorb a recorded surplus.

## OrderDetailsIntent

```csharp
public sealed record OrderDetailsIntent(
```

The order-level facts the till collected: how it is fulfilled, and the contact and the requested
time that go with that.
<param name="OrderType">
Whether the customer eats here or takes it away. DECIDES WHETHER THE PHONE IS KEPT — see
`Order.CustomerPhone`.
</param>
<param name="CustomerPhone">Whatever was typed. Normalised here, and DROPPED for counter service.</param>
<param name="RequestedAt">What the customer asked for, or `null` for as soon as possible.</param>

A separate parameter rather than three optionals on the line list, because these are facts about
the ORDER, not about any line: a customer either takes the whole thing away or eats all of it on
the premises. Carrying them per line would let one order be half takeaway.
The phone is accepted as free text and normalised at the service boundary, never stored as typed.
E.164 is the storage and transmission format, E.123 the display one; normalising now is cheap and
normalising later is a migration over every row that already holds free text.
A phone in this record is NOT a subscription of any kind: Square is explicit that typing a number
does not sign the customer up for SMS, and this app sends no messages at all. The field records a
contact for one order and nothing else.

## CheckoutAsync

```csharp
Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);
```

Creates the order and writes off the ingredients in ONE transaction:
either both the order and the stock change exist, or neither does.
Order numbers are allocated atomically per shift.
Payment is deferred: the order starts unpaid. The overloads below take the payment and the
order-level details instead — they do not replace this one, because the shape every existing
caller (and its tests) passes stays the one that works.

## PaymentRecorder

```csharp
internal static class PaymentRecorder
```

The one place where the order's net cash moves: it adds the ledger row AND moves
`Order.PaidKopecks`. There are now four writers — checkout,
`IOrderService.AddPaymentAsync`, `IOrderService.RefundAsync` and the automatic refund
inside a cancellation — and they must not drift, because the scalar is what every screen reads
and the rows are what the shift report and an auditor reconcile against; a change that touched
only one of them would leave the books open or closed by exactly one movement.
`Record` adds and `Refund` subtracts, and they deliberately invert on
all three behaviours: Record throws on a cancelled order, throws when there is no balance left
and ADDS; Refund throws unless the order is closed, clamps to what was collected and
SUBTRACTS. Two methods rather than one method with a sign — a negative amount must never reach
`Money.ToKopecks`, because a negative `AmountKopecks` stored in the ledger would
break the positive-amounts rule that every sum and report depends on, and the direction belongs
in `IsRefund` anyway.
Neither method calls `SaveChangesAsync`. Checkout runs inside a transaction that also
writes the order and the stock write-off, and a cancellation refunds inside the transaction that
also voids the order — the caller's SaveChanges/Commit is the only commit point. Saving here
would flush the half-built work on its own and defeat that transaction.
Neither pushes the row into `order.Payments`. The order is normally an already
tracked entity, and an entity pushed into the loaded collection of a tracked parent is tracked
as Modified: EF then issues an UPDATE for a row that does not exist yet (the bug recorded in
DraftOrderService and in UpdateOrderAsync). Going through the DbSet marks the row Added, and EF
orders the INSERT after its parent.

## applied

```csharp
var applied = Math.Min(Money.ToKopecks(amount), order.BalanceKopecks);
```

Cap at the balance, never record a surplus. A POS is handed a 1000 ₽ note for a 660 ₽
order: recording 1000 would break the till arithmetic (1000 in, 340 change out, 1000 in
the ledger) and there is no till/petty-cash model to absorb the difference. The payment
sheet shows the change itself and passes only the applied amount; this is the defensive
clamp that keeps the books true no matter what a caller does.

## Refund

```csharp
public static IReadOnlyList<OrderPayment> Refund(
```

Books <paramref name="kopecks"/> back out of the till and returns the ledger rows (one per
source payment it mirrors). Nothing is written to the database — the caller saves.
WHY the method comes from the ledger and never from the operator: a POS has two tills — the
drawer and the card terminal — and a refund has to leave the one the money arrived in. If
200 ₽ came in cash and 220 ₽ by card, a 420 ₽ refund booked as cash leaves the drawer 220 ₽
short at the count and credits the terminal money it never paid out, and nothing in the app
would ever notice. So the walk below is FIFO over the order's collected payments, which also
gives `refunded(method) &lt;= collected(method)` for free: no per-method balance can go
negative without someone having to check it. There is deliberately no operator-chosen refund
method in this iteration — card payments here are a declared figure with no terminal behind
them, so a chosen method would be a bookkeeping entry with nothing physical to reconcile.
<paramref name="allowUncompletedOrder"/> exists because the status guard is a rule about the
standalone refund operation, not about the money. Money goes back after the goods left the
bar, which is why a refund on a Ready order is refused: the goods have not left yet, so
cancelling the order is the operator's decision, not refunding a finished sale. A
cancellation is the strictly stronger action (it voids the sale and the shift report drops
the order), so it must not be blocked by the guard of the refund it performs on the way.

## requested

```csharp
var requested = Math.Min(kopecks, order.PaidKopecks);
```

Cap at what was actually collected. This one guard is what keeps Order.PaidKopecks from
ever going negative, which in turn keeps BalanceKopecks, PaymentState and the
PaidKopecks <= TotalKopecks assertion honest — a scalar below zero would read as a debt
larger than the order and as "not paid" on a sale that is already finished.

## ConflictException

```csharp
throw new ConflictException("Не удалось определить, каким способом была оплата: возврат невозможен.");
```

Unreachable through the app (every paid order has at least one ledger row), but if the
scalar ever says money was collected and the ledger cannot say by which method, the
honest answer is to refuse — not to invent a method the till never used.

## applied

```csharp
var applied = Math.Min(slice.Kopecks, order.PaidKopecks);
```

The cap is applied again per slice, which is what makes the invariant hold no matter
what the caller asks for: the slices sum to at most what is left, so the scalar
cannot be driven below zero even if the ledger and the scalar disagreed.

## TruncateNote

```csharp
private static string? TruncateNote(string? reason)
```

OrderPayment.Note is declared as 300 characters (AppDbContext.Catalog), and SQLite would
happily store a page of text in it anyway — so the clamp lives here rather than in the
column, where it would only show up as a value that disagrees with the model.

## AllocateMirroredSlices

```csharp
private static List<Slice> AllocateMirroredSlices(AppDbContext db, Order order, long kopecks)
```

Splits a refund across the methods the money actually arrived in, oldest payment first, and
never more than a given source payment still holds.
The refunds that were already booked are replayed against the same sources rather than being
summarised per method, and that is what makes a SECOND refund on the same order stay inside
the original payments: refund 100 of a 200 ₽ cash payment, then ask for 150 more, and the 150
comes out of what is left of the cash payment and then of the card one — it does not bill the
drawer for a second 100 it never received.
Consumption is scoped to the source's own METHOD, not just to "some source with money left".
A cash refund can only ever have come out of a cash payment, so matching on the method keeps
the per-till balance exact rather than merely correct in total, and makes the result
independent of the order two refunds that share a timestamp happen to be read in.

## ledger

```csharp
var ledger = db.OrderPayments.Where(payment => payment.OrderId == order.Id).ToList();
```

Synchronous on purpose: Refund is a synchronous unit of work by contract (the caller is
inside an async path and awaits it as one step). SQLite cannot ORDER BY a DateTimeOffset
column in SQL — the same reason the order lists and GetOrderPaymentsAsync sort in memory —
and the ledger of one order is small enough for that to be free. IX_OrderPayments_OrderId_
PaidAt serves the WHERE.

## pending

```csharp
var pending = db.OrderPayments.Local
```

A LINQ query goes to the database, which knows nothing about rows this context has Added
but not yet saved. Unioning the local ones is what makes a second Refund call in the same
unit of work see the first one's rows; the distinct keeps the already-tracked originals.
