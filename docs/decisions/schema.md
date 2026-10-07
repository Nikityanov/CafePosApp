# Схема и миграции

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## ComboSlotOption

```csharp
public sealed record ComboSlotOption(Guid ProductId, string Label, long UnitKopecks, long ReferenceKopecks);
```

A dish a bundle slot may be filled with, as the composition sheet offers it.
<param name="ProductId">
The SLOT's dish, never the substitute's. `ResolveOne` matches an incoming component to a
catalogue slot by the slot's `ProductId` and refuses anything it cannot match, so a cart
carrying the substitute's identifier would be refused as "a dish that is no longer part of this
bundle". Only the label and the two prices describe the substitute.
</param>
<param name="Label">
What the sheet prints. Pre-composed by the caller rather than assembled here, because the only case
that needs composing is a substitution - the slot's dish ran out and this is the replacement - and
the words for that belong next to the rule that decides it. See `BundlePlan`.
</param>
<param name="UnitKopecks">
What one of this dish is charged inside the bundle, in kopecks, already resolved by
`ComboPricing.ResolveUnitKopecks`: a slot priced explicitly keeps that
price, otherwise the dish's own price is used. The sheet shows a running total from these, and the
price that is actually charged is recomputed from the catalogue at checkout.
</param>
<param name="ReferenceKopecks">
What this dish costs on its own, in kopecks - Simphony's "Prep Cost", and the figure
`OrderItemComponent.ReferencePriceKopecks` keeps beside
`UnitPriceKopecks` so that one sale can answer two reports. The sheet shows what the bundle
saves against it.
</param>

MOVED HERE FROM `CafePos.Presentation/Services/Abstractions/PlatformServices.cs`, beside
`IComboEditor` which still lives there. The record says nothing about how it reaches
the screen — it is a fact about a bundle — and it was untestable from the Tests project while it
sat in a file about sheets and dialogs.

## ComboEditorRequest

```csharp
public sealed record ComboEditorRequest(
```

What the composition sheet opens on: the dishes it may offer, what is in the sheet already, and
the bundle's own price.
<param name="Title">Sheet title, naming the bundle.</param>
<param name="Options">The dishes a slot may hold. See `ComboSlotOption`.</param>
<param name="Selection">The slots already in the bundle; empty for a custom build.</param>
<param name="PriceKopecks">
The bundle's own price, in kopecks — what the till charges. Shown as the sheet's main figure,
with the à la carte sum of the slots as the reference it is measured against. Zero when the
caller does not know it (a custom build from scratch), in which case the sheet shows the sum
alone.
</param>

ONE REQUEST SHAPE FOR BOTH CASES, and that is the whole point: a catalogue bundle arrives with a
filled `Selection` and `Empty` arrives with none. They are the
same sheet, the same rows and the same confirm — a custom build is not a second mechanic, it is
this one started empty.

## Currency

```csharp
public sealed record Currency(string Code, string Title, string Symbol, int MinorUnitDigits, string MinorUnitName)
```

A currency the till can display: its ISO code, the sign to print, and how many minor-unit
digits it has.
<param name="Code">ISO 4217 code, also the persisted setting value.</param>
<param name="Title">Name for the settings list, in the app's language.</param>
<param name="Symbol">The sign printed after the amount.</param>
<param name="MinorUnitDigits">Digits after the decimal separator: 2 for kopecks/cents, 0 for yen.</param>
<param name="MinorUnitName">Name of the minor unit, shown in the settings list for clarity.</param>

WHY THE MINOR UNIT IS DISPLAY-ONLY
==================================
Amounts are persisted as integer hundredths (see `Money`), because SQLite stores
decimals as TEXT and that breaks SUM/ORDER BY. So the storage grid is fixed at 1/100 of a unit
no matter which currency is selected, and `MinorUnitDigits` only decides how those
hundredths are PRINTED. Every preset with 2 digits maps one-to-one onto the stored value, so
switching currency never reinterprets a stored price — it only relabels it. That is deliberate:
a till must not silently reinterpret the money in the drawer when someone changes a setting.
A currency with 3 minor digits (KWD, BHD, OMR, JOD) cannot be represented exactly on a
hundredths grid, so those are deliberately absent from `Currencies` rather than
silently rounded. 0 digits (JPY) is exact the other way: it drops the sub-unit at display time,
which is what a Japanese price tag does.

## Default

```csharp
public static Currency Default { get; set; } = Ruble;
```

This is an ambient default on purpose, and it is the same pattern .NET itself uses for
`TimeProvider.System`. The alternative — threading a currency through fifty-odd call
sites — buys purity at the price of a domain that has to be told about a presentation
setting on every call, and it still would not reach the two Core display properties
(`Models.Ingredient.CostPerUnitText` and
`Models.PriceHistoryEntry.ChangeText`) that are bound straight from XAML and
have nowhere to receive a parameter.
It is mutable, so it is a hazard for tests: a test that sets it must reset it, and the
existing TextFormat tests are written against `Ruble`, which is the default, so
they hold without touching this. Only the app sets it in anger
(see Services.CurrencySelection).

## BelarusianRuble

```csharp
public static readonly Currency BelarusianRuble = new("BYN", "Белорусский рубль", "₿", 2, "капеек");
```

Belarusian ruble. Its sign is U+20BD, added in Unicode 14.0 (2021), so it is the one symbol
here that a bundled Open Sans build may not carry — Android and iOS both fall back to a
system font for a missing glyph, and this was checked on an API 36 emulator rather than
assumed. One unit is 100 kapeyek, hence 2 digits, exactly like the Russian ruble.

## All

```csharp
public static readonly IReadOnlyList<Currency> All =
```

Every preset, ordered so the two rubles sit at the top and the rest follow by region
relevance to a café POS. JPY is included deliberately: it is the only 0-digit entry, so it
is what proves the minor-unit setting actually does something rather than being decorative.

## FromCode

```csharp
public static Currency FromCode(string? code)
```

Resolves a persisted code back to a currency. An unknown or missing code falls back to the
Russian ruble rather than throwing: the value comes from device preferences, and a
preferences file that has been hand-edited, restored from an older build, or truncated
must not be able to stop the till from opening.

## ProductAddFlow

```csharp
public sealed class ProductAddFlow
```

The decisions of the "add a product to the cart" flow: modifier sheet → variant sheet → price.
The sheets are platform UI, so this type never awaits anything: the caller passes in what each
picker returned and gets back the verdict. That split is the point — the three cancel paths are
the part of the flow that actually goes wrong, and none of them needs a UI to be reasoned about.

## CanOfferVariantChoice

```csharp
public bool CanOfferVariantChoice => !RequiresVariantChoice || AvailableVariants.Count > 0;
```

Whether the variant step can be completed at all. A product flagged as having variants whose
variants are all sold out has nothing to show and no price to charge, so the add fails
outright. That is a different outcome from a dismiss and the caller has to say why.

## ResolvePrice

```csharp
public decimal ResolvePrice(string? variant) => variant is null
```

The price the cart line is charged. A chosen variant replaces the product price outright
rather than adding to it; an unrecognised name (a picker that returned something the catalog
does not have) falls back to the product price instead of charging zero.

## HasMany

```csharp
entity.HasMany(combo => combo.Components)
```

PriceKopecks keeps the default INTEGER mapping with no converter, like Product's: it is
money the catalogue already speaks in kopecks, and a conversion would only be a second way
to spell the same integer. It is NOT NULL, and SaveComboAsync refuses zero — a sellable
item at no price is a catalogue error, and a NULL here would have to become 0 at every
read, i.e. a second silent place where a missing price turns into a free meal.

## Без названия

```csharp
});
```

No index on IsDeleted, unlike Product: a catalogue of bundles is a handful of rows
where the menu screen reads all of them anyway, and a second single-column index here
would be a column SQLite has to keep in step with one it never chooses.
And no index on PriceKopecks for the same reason stated there: nothing queries a bundle
BY its price, so an index on it would be a second column SQLite keeps in step with one
it never chooses. Migration012 therefore creates no index at all — the model declares
none for Combos, and the parity test reads columns, so nothing would ever notice.

## HasOne

```csharp
entity.HasOne(component => component.Product)
```

Two references to Products, so both navigations are named: the slot's dish and its
substitute. ComponentPriceKopecks keeps the default nullable long? mapping — its three
states (null = the dish price, 0 = free, a number = this price) are exactly what makes
it usable, and a non-nullable default of 0 would collapse "free" into "the dish is
free", which is a different statement about the catalogue.

## Без названия

```csharp
});
```

SubstituteProductId gets EF's conventional index as a foreign key, and Migration010
creates it by name: it is never queried on its own (a sale loads the bundle's slots),
but a fresh install would have it and an upgraded one would not.

## Property

```csharp
entity.Property(order => order.OrderType).HasConversion<string>().HasMaxLength(30);
```

TEXT like Status and OrderPayment.Method, so an order's fulfilment mode is readable
straight out of SQLite during an investigation. 24 characters is the E.164 cap plus
room for the '+', and the value is written by PhoneNumber.Normalize, never typed free.

## HasIndex

```csharp
entity.HasIndex(order => order.RequestedAt);
```

The schedule section reads this column, and it is also what makes a later move to SQL
ordering possible without a migration. Useless on its own today: SQLite cannot ORDER BY
a DateTimeOffset, so the queue sorts in memory over a materialised projection.

## Без названия

```csharp
});
```

ListPriceKopecks keeps the default INTEGER mapping with no converter, like
DraftOrder.IsActiveCart: it is money the product price already wrote, so a conversion
would only be a second way to spell the same integer.
OrderItemComponent.ProductId is deliberately NOT a foreign key. The snapshot has to
outlive the catalogue entry it names — a sale does not stop having happened because the
dish was later renamed or removed.

## Property

```csharp
entity.Property(payment => payment.Note).HasMaxLength(300);
```

IsRefund deliberately keeps the default bool mapping (INTEGER), no converter — the
same reasoning as DraftOrder.IsActiveCart. Every row that exists before the refund
feature is a collection, so the column's DEFAULT 0 is already the correct backfill
and a value converter would only add a second way for a zero to be spelled.

## HasIndex

```csharp
entity.HasIndex(payment => new { payment.OrderId, payment.PaidAt });
```

Payments of one order are read in payment order; the index serves the details screen,
the migration's NOT IN (SELECT OrderId ...) reconciliation check and the refund
walk, which reads one order's non-refunded rows in PaidAt order (FIFO mirroring).

## Property

```csharp
entity.Property(shift => shift.CashDiscrepancyReason).HasMaxLength(300);
```

Declared length matches OrderPayment.Note (300). CloseShiftAsync REFUSES an over-long
discrepancy reason rather than truncating it, the opposite of PaymentRecorder's note
handling on purpose — this one is the only record of why a drawer did not balance.

## Без названия

```csharp
});
```

CountedCashKopecks / ExpectedCashKopecks / ReconciledAt keep the default nullable long?
/ DateTimeOffset? mapping. No value converter: NULL means "never counted", and a
converter or a 0 default would erase that distinction — a counted 0 is missing money,
while NULL is a shift nobody stood at the till for.

## HasIndex

```csharp
entity.HasIndex(movement => movement.ShiftId);
```

Every read of this table is "this shift's movements", both for the balance and for the
list on screen, so the index is on ShiftId alone and not on ShiftId + CreatedAt: the
balance sums without an order and the list orders in memory, and a composite index
would only make the second read pay for a sort the first one does not use.

## Без названия

```csharp
});
```

ReversesMovementId is deliberately NOT a foreign key. A self-reference that SQLite
enforces would mean deleting a movement — which nothing in the app does, but a
hand-edited database could — silently cascade a second deletion or fail the write.
The link is checked in CashLedgerService, where the refusal can be explained.

## ConfigureCatalog

```csharp
ConfigureCatalog(modelBuilder);
```

NOTE: SQLite stores decimals as TEXT, hence:
* every monetary value is persisted as INTEGER kopecks (see Common/Money.cs);
* fractional quantities (stock, recipe amounts) keep decimal storage, but all
comparisons/orderings on them happen in memory (see InventoryService).

## DatabaseBootstrapper

```csharp
public sealed class DatabaseBootstrapper(
```

Prepares the local database on startup: schema migration, active shift, demo data.
`InitializeAsync` is the single shared entry point: N callers (App startup plus
every page that reads SQLite) all await ONE run instead of racing their own. Two guarantees
matter to callers:
<list type="bullet">
<item>Concurrent callers get the same in-flight task, and a completed run is never repeated.</item>
<item>A **failed** run is evicted from the cache, so the next caller retries.</item>
</list>
Retry (rather than caching the failure and surfacing it forever) is the deliberate choice for
this app: it is an offline single-device POS, the usual startup failures are transient
(file lock, disk busy, antivirus) and the operator needs the till to come up on the next tap.
Caching a fault would leave the app permanently broken until a reinstall, with no operator
action able to clear it. The failure is still surfaced — it is thrown to the caller and
logged — it is simply not latched, so navigating to another tab retries the migration.

## db

```csharp
await using var db = await factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
```

NO SHIFT IS CREATED HERE, and removing that call is one of the three places this change
touches. Startup used to guarantee an active shift, which meant a terminal that was merely
RESTARTED came back with a shift that nobody opened and a drawer nobody counted — and no
place to record the change in it, because the shift already existed. A terminal between
shifts is now a normal state that the opening screen handles.

## LeadTimeMinutes

```csharp
public int LeadTimeMinutes { get; init; } = Models.Order.DefaultLeadTimeMinutes;
```

Minutes an order with no requested time is promised to take — what the menu shows as "about
10 minutes" and what the queue sorts against. Assigned once at startup to
`Models.Order.LeadTimeMinutes`.
It lives here and not in a table of settings because the app has no settings store, and a new
table is not the price of one number. It is read at startup and never from the database, so an
order can never have its promise recomputed after the fact.

## LeadTimeMinutes

```csharp
Order.LeadTimeMinutes = options.LeadTimeMinutes;
```

One assignment, once, at startup: the promise an order without a requested time is given.
It is ambient (see Order.LeadTimeMinutes) rather than injected because PromisedAt is read
straight from markup with nowhere to receive a parameter, and it is read at startup so that
a promise computed later cannot disagree with the menu that quoted it.

## services

```csharp
services.AddSingleton<OrderService>();
```

One OrderService registered as the concrete singleton and re-published under each port it
satisfies, rather than seven separate registrations that would each try to construct their own.
OrderService is a primary-constructor class over one DbContext: sharing the instance is the
point, and a scoped DbContext behind seven transient wrappers would be a worse bug than the flat
interface was.

## IsPermissionException

```csharp
_ when IsPermissionException(exception) => $"{prefix}: недостаточно прав приложения на устройстве. Подробности в журнале.",
```

Matched by name, not by type: MAUI's PermissionException lives in a MAUI assembly
and CafePos.Core is deliberately built on plain net10.0 (see CafePos.Core.csproj).
It derives from UnauthorizedAccessException, so without this arm a missing platform
permission was reported as "нет доступа к файлу или папке" — naming a filesystem
the operator never touched. Naming the arm also keeps the ordering explicit: a
derived platform exception must be listed before the base one it derives from.

## CashDifference

```csharp
public enum CashDifference
```

What a physical count of the drawer turned out to be against what the ledger said it held.
Three named states and no amounts: the amounts belong to
`CafePos.Core.Services.CashReconciliation`, which derives them from the counted and
the expected figures. A named difference is a READING of those two numbers for a screen and a
log line — a fourth copy of the same arithmetic would be a fourth thing to keep in step.

## Combo

```csharp
public class Combo
```

**THE PRICE IS A NUMBER ACCOUNTING PUT IN THE CARD, NOT A SUM THE TILL COMPUTES.** This was the
other way round first — a bundle had no price at all, the price was the sum of its slots, and
raising the price of a dish silently repriced every bundle containing it. The owner reversed that,
and the reason is ownership of the decision, not convenience: **accounting decides the price and
the discount, the till records it**. In Oracle Simphony this is the documented alternative to
"Add Side Prices To Meal Price" — the price is stated in the menu item definition and does not
follow the parts. A program that computes the price of a meal has taken the pricing decision away
from the person entitled to it, and no amount of documentation puts it back.
So the component sum survives, but under a different name and a different meaning: it is the
à la carte REFERENCE the bundle is measured against (see `ComboPricing.ReferenceKopecks` and
`ComboPricing.DiscountPercent`), and it never reaches the customer. Three consequences are
load-bearing, not side effects:
<list type="bullet">
<item>Changing a dish price does not reprice the bundles containing it, and a bundle MAY be dearer
than its parts — that is a surcharge, it is legal, and it is reported as one.</item>
<item>Substituting an unavailable component does not move the price. D365's "if the replacement is
dearer, the kit price is recalculated" is a rule of a model that computed the price from the
parts; under a fixed price the difference is the café's to absorb. What was actually served is
still written to the line's composition snapshot, because the kitchen and any later audit need to
know it.</item>
<item>A slot priced 0 no longer makes that part of the bundle free to the customer. It still
changes the reference, which is what a free slot is for: "the croissant in this bundle is on the
house" is a statement about the comparison, not about the charge.</item>
</list>
A combo is also not a `Product`: it has no stock of its own and no 86 — its slots do.
Square, Simphony, Lightspeed and D365 all draw the same line ("components are the truth, parent
is a rollup"), and an ingredient that belongs to two combos is written off once per sale of each,
which is why `ComboExpander` expands a combo before the planner sees it.
Soft delete for the same reason as `Product.IsDeleted`: sold combos stay on old
receipts, so removing one must not remove it from history.

## PriceKopecks

```csharp
public long PriceKopecks { get; set; }
```

What the till charges for one unit of this bundle, in kopecks.
Integer kopecks like every other price in this model, never rubles and never `decimal`:
this number is compared with the price a line was charged and is the allowed side of the one
price control in the app (`Common.OrderLinePricing`), and a rounding step inside
that comparison is a kopeck of drift per line.
NOT NULL and refused at save when it is zero or negative (see
`IComboService.SaveComboAsync`): a sellable item at no price is a catalogue error, not a
bargain, and a NULL here would have to become 0 at every read — a second, silent place where a
missing price turns into a free meal.

## Components

```csharp
public List<ComboComponent> Components { get; set; } = new();
```

The slots this bundle is made of. At least one, in practice: a bundle with no component is a
card with nothing to sell.
They no longer determine the price — `PriceKopecks` does — but they are what the
availability, the stock write-off and the à la carte reference are all computed from, so they
are not decoration either.

## ComboComponent

```csharp
public class ComboComponent
```

WHY THE SLOT HOLDS A `Product` AND NOT AN OPTION. A component has to be a real dish
with its own stock, its own 86 and its own line in the reports, and the component list on a sold
order is a snapshot of exactly that. This is the majority design (Square, Simphony, Lightspeed,
D365) and the alternative — options inside the bundle — is what D365 has to work around by
enumerating every combination, which is the combinatorial blow-up behind "you cannot copy groups"
in Simphony.
WHY THERE IS NO NESTING. No vendor in the surveyed set supports a bundle inside a bundle
(Shopify: "Nested bundles aren't supported"). A slot pointing at another combo would make the
price a recursive walk and the availability question unbounded, so a slot is always a dish.

## ProductId

```csharp
public Guid ProductId { get; set; }
```

The dish in this slot. Restricted on delete: a dish that is part of a bundle must not
disappear under it. Products are soft-deleted anyway, so this guard is for hand-edited
databases and for the day a hard delete is ever introduced.

## QuantityPerUnit

```csharp
public int QuantityPerUnit { get; set; }
```

How many of this dish one unit of the bundle takes. Always at least 1.
The multiplicity lives HERE and is never derived from the cart line quantity: a quantity of 3
on a combo line means three complete bundles (every vendor agrees), so the per-unit
multiplier has to be a property of the slot or a second croissant would be impossible to
express.

## ComponentPriceKopecks

```csharp
public long? ComponentPriceKopecks { get; set; }
```

What this slot is charged inside the bundle, in kopecks, or `null` to charge the dish's
own price. The three states are deliberately distinct: `null` = the dish price, 0 = free,
a number = this price instead.
Simphony's default is the same shape ("Add Side Prices To Meal Price"), and the price
REPLACES the dish price rather than adding to it — Aloha's "Item price" method, and what
`Common.ProductAddFlow.ResolvePrice` already does for a chosen variant.

## SubstituteProductId

```csharp
public Guid? SubstituteProductId { get; set; }
```

What to sell instead when this dish is sold out, or `null` when there is nothing to
fall back to.
Substitution rather than blocking the sale, as every serious vendor does (Simphony
substitution groups, D365 product substitutes). D365 also re-prices the kit when the
replacement is dearer, and that part is DELIBERATELY NOT IMPLEMENTED: a bundle's price is
stated in its own card (`Combo.PriceKopecks`) and does not follow the parts, so the
difference between the two dishes is absorbed by the café. The replacement is still recorded on
the sold line's composition snapshot, at its own price, because that is what was served.
With no substitute, the sale is refused naming the dish rather than quietly shipping something
else.

## DraftOrder

```csharp
public class DraftOrder
```

The order-level facts the till collected are mirrored here — `OrderType`,
`CustomerPhone`, `RequestedAt` — so that parking and forgetting does not
throw away what the operator entered. A parked cart is not an order: it is in no queue, it is
promised to nobody and it is reconciled against no drawer, which is why the promise arithmetic of
`Order.PromisedAt` is deliberately NOT duplicated here. Nothing would read it, and a
second copy of a derived figure is a second thing to go stale.

## Components

```csharp
public List<DraftOrderItemComponent> Components { get; set; } = new();
```

The composition of a bundle line, carried through the parking lot exactly as
`OrderItem.Components` carries it through checkout. Rewritten with the line on
every autosave, which is why it is a separate table and not a column.

## DraftOrderItemComponent

```csharp
public class DraftOrderItemComponent
```

A duplicate of `OrderItemComponent` on `DraftOrderItemId` rather than a
shared parent, because the two rows are written by different operations at different times: a
draft is rewritten wholesale on every cart autosave (see `DraftOrderService`, where old lines
are deleted and re-inserted), while an order item's snapshot is written once, inside the
checkout transaction, and never rewritten. Sharing a table would put those two lifetimes in one
cascade and make an autosave touch order history.
The columns mirror the order-side ones exactly, including both prices, so that taking a parked
cart and checking it out produces the same snapshot a cart that was never parked would — the
operator typed those prices once and must not lose them to a round trip through the parking lot.

## NotMapped

```csharp
[NotMapped]
```

Goes through `TextFormat.CostPerUnit(decimal, string?)`, which resolves
`Currencies.Default`, so the symbol follows the operator's setting with no
parameter to thread through a model that is bound straight from XAML.

## ListPriceKopecks

```csharp
public long ListPriceKopecks { get; set; }
```

The price this line was ALLOWED to be sold at, in kopecks: the catalogue price before any
manual review. Immutable once written.
For a bundle that is the bundle's OWN price (`Combo.PriceKopecks`) — the number
accounting put in the card — and NOT the sum of its slots. A line added to an order whose
template no longer exists has no catalogue price to be had, and is compared against its à la
carte sum instead; that is the one weaker case, and it is why the figure is written once and
never derived from the composition on read.
`PriceKopecks` is what was actually taken and may be changed by hand;
`PriceKopecks != ListPriceKopecks` therefore means "a price was overridden", for ANY line
and not only for a bundle. That makes the shift report's discount section a detection control
that costs the till nothing: the first override shows up in the same report as the shift,
with no historical threshold to calibrate and no PIN path to bypass.
The alternative was a separate "adjustments" log, and it is the weaker choice for a reason
worth writing down: a check has to live where every change passes through. A log written by one
branch is bypassed by the branch that writes the price instead (Square published exactly such a
bypass), whereas a second column on the line is written by the same statement that writes the
price.
For bundles there is a second, independent signal:
`Σ(Components.ReferencePriceKopecks)` — what the same dishes would have cost on their own.
One signal is "the operator changed the price", the other is "this bundle was cheaper than its
parts", and the report can show both without either having to be trusted alone. A bundle priced
ABOVE its parts is a surcharge: the second signal goes negative, which is a fact about the
bundle's pricing and not an override.

## Components

```csharp
public List<OrderItemComponent> Components { get; set; } = new();
```

What this line was made of, for a bundle — one snapshot row per slot. Empty for an ordinary
dish, which is what makes "is this a combo" answerable without a flag.
DISPLAY ONLY: like `Order.Payments`, populate it with an explicit
`Include` (or on a freshly added parent). Nothing in the money path reads it — the line
total is `LineTotalKopecks` either way — and the order lists load items without
their components, so an un-Included collection is empty rather than stale.

## OrderItemComponent

```csharp
public class OrderItemComponent
```

**WHY `ProductId` IS HERE BUT IS NOT A FOREIGN KEY.** The identifier is kept so a
later report can ask "how many bundles with this dish sold this month", but the rows must survive
the dish: this table is a record of a sale, and a sale does not stop having happened because the
dish was renamed or removed from the catalogue. Every figure the reports need — the name, the
price charged, the à la carte price — is stored on the row, which is why an old receipt can be
reprinted unchanged. See the same reasoning on `OrderItem.ProductName`.
**WHY BOTH PRICES ARE STORED.** `UnitPriceKopecks` is what this slot contributes to
the bundle's à la carte reference — the slot's own price, or the dish's when the catalogue states
none; `ReferencePriceKopecks` is what the same dish costs on its own. Simphony
keeps Price and Prep Cost side by side for exactly this reason: one transaction has to answer two
different reports — how many bundles sold, and what they would have cost separately. With only one
of the two, the discount on a bundle cannot be shown to the customer and cannot be reported on
later. Neither figure is what the customer was charged: that is
`OrderItem.PriceKopecks`, the bundle's own price. The second signal is also
independent of `OrderItem.ListPriceKopecks`: one is "the price was overridden", the
other is "this bundle was cheaper than its parts".

## UnitPriceKopecks

```csharp
public long UnitPriceKopecks { get; set; }
```

What this slot is worth inside the bundle, in kopecks — Simphony's Price, and the figure the
à la carte reference is built from. Free slots are 0, not null: null meant "the dish price"
back in the catalogue, and on the sale it is already resolved. It is NOT what the customer was
charged, and a substituted slot carries the substitute's own figure here.

## OrderPayment

```csharp
public sealed class OrderPayment
```

One payment received against an order — the ledger behind `Order.PaidKopecks`.
The two must always agree, so every write goes through the single mutation site
(`Services.PaymentRecorder`), which adds the row and moves the scalar together.
A refund is the same row shape with `IsRefund` set, not a separate table: the
reconciliation invariant is a signed sum over one ledger, and a second table would make every
report that forgets to join it over-report the money that came in.

## OrderId

```csharp
public Guid OrderId { get; set; }
```

Explicit CLR property rather than a shadow foreign key. A shadow property still produces a
column, but it is invisible in the entity, in code navigation and in the model/migration
parity check that reads the EF model — which is exactly why the parity test counts shadow
properties as columns. Declaring it keeps the FK visible where the code reads it.

## AmountKopecks

```csharp
public long AmountKopecks { get; set; }
```

Amount actually applied, in kopecks, never above the balance of the order at the moment of
the payment. A POS is regularly handed a 1000 ₽ note for a 660 ₽ order; the surplus is
change the UI shows and does not pass in, and the clamp here is the defensive second line.
Positive on every row, refund rows included. A refund is a row that pays money OUT, never a
negative receipt: `IsRefund` carries the direction. Keeping the amount positive
means the invariant stays a plain signed sum, and no report that forgets to filter can
accidentally double-count by treating a refund as money coming in.

## IsRefund

```csharp
public bool IsRefund { get; set; }
```

This row pays money OUT of the till rather than into it — money that came back to the
customer. Stored as a plain SQLite INTEGER with no value converter, following
`DraftOrder.IsActiveCart`: the whole point is that "IsRefund = 0" is the correct value
for every row that already exists, so no backfill and no enum-name strings are involved.
A refund mirrors the methods of the payments it reverses (see
`OrderService.RefundAsync`), so it is never an operator-chosen method.

## OrderType

```csharp
public enum OrderType
```

WHY AN ENUM AND NOT A BOOLEAN. A "isTakeaway" flag would be one more independent truth that the
phone question then has to be reconciled against, and the two could disagree — an order with a
phone and no takeaway, which is exactly the row 152-ФЗ ст. 5(4)-(5) calls redundant. Tying the
contact to the fulfilment mode instead of to the typing of a number is what makes "we do not
store it" a property of the order rather than a promise: CounterService stores no phone at all,
which is the less intrusive alternative EDPB Guidelines 2/2019 п. 25 says makes the processing
unnecessary in the first place.
There is no "Other" and no "Delivery": every member has to be one an operator can act on at the
till, and a value this build does not know must read as itself rather than quietly become the
least intrusive member.
Persisted as TEXT, like `OrderStatus` — readable straight out of SQLite during an
investigation instead of being an ordinal only this build understands. The member names are
therefore part of the stored data and are never renamed.

## PaymentMethod

```csharp
public enum PaymentMethod
```

How money was received. Persisted as TEXT (see ConfigureOrders), and deliberately a closed
set: a POS has exactly two tills (the drawer and the terminal), so a new method is a code plus
migration change, never a free-text entry an operator could typo into the ledger.

## PaymentState

```csharp
public enum PaymentState
```

Payment state of an order. Derived, never persisted, and deliberately NOT called PaymentStatus:
in this codebase a *Status name means a stored column (Order.Status is the string
"InProgress"/"Ready"/...). A [NotMapped] PaymentStatus would be read as "just not saved yet"
and would eventually be "fixed" with a column that can then disagree with the ledger — the same
class of bug as deriving money from a collection instead of a scalar.

## NotMapped

```csharp
[NotMapped]
```

Whole units on both sides, deliberately: this is a price-change audit line, and "200 → 250"
is what a manager reads. The symbol comes from `Currencies.Default` via
`TextFormat`, so it follows the operator's setting.

## ISchemaMigration

```csharp
public interface ISchemaMigration
```

One ordered, idempotent schema step. Implementations must be safe to run on a database
that already contains user data (cash register!), therefore every step checks the
current schema before touching it and never drops user data silently.

## Migration001_Baseline

```csharp
internal sealed class Migration001_Baseline : ISchemaMigration
```

Version 1 — brings any legacy database (created by the old EnsureCreated + ALTER TABLE
hybrid, at any intermediate revision) up to the "structural baseline" of the model and
removes the dead variant tables that the removed VariantGroup/VariantOption models left behind.

## Migration003_UtcTimestamps

```csharp
internal sealed class Migration003_UtcTimestamps : ISchemaMigration
```

Version 3 — every timestamp is stored in UTC with an explicit "+00:00" offset.
The app used to write DateTime.Now values without any offset, so the provider parsed them
back as local time and reports silently shifted across time zones / DST changes.

## Migration006_OrderPayments

```csharp
internal sealed class Migration006_OrderPayments : ISchemaMigration
```

Version 6 — payment ledger: Orders.PaidKopecks plus the OrderPayments table.
Order matters and each step is individually idempotent: the column first (with a literal
DEFAULT 0, so SQLite's ADD COLUMN is O(1) and needs no table rewrite), then the empty table,
then the backfill. SchemaMigrator wraps a migration in a transaction and SQLite DDL is
transactional, so a crash mid-way leaves neither the column, nor the table, nor the version row.
WHY the backfill exists, and why it is a product decision rather than an oversight: with
DEFAULT 0 every pre-existing open order would silently become a debtor. It would then be
refused at Ready, it would inflate the shift-close refusal, and it would fabricate debt that
never existed — payment deferral did not exist before this feature, those orders were paid at
checkout. So every non-cancelled order is marked fully paid AND gets one matching synthetic
Cash row. Both or neither: the column without the row breaks the reconciliation invariant
(PaidKopecks == SUM(payments)) and makes every historical order read as "paid by an unrecorded
cash payment", which is exactly the hole an auditor flags first. Cancelled orders stay at 0 —
they were never collected.

## ExecuteAsync

```csharp
await SqliteSchemaHelper.ExecuteAsync(db,
```

3. Backfill the scalar, then the matching ledger row.
The status predicate is written against a CAST because Orders.Status is not stored the
same way in every database. Ones created from the current model have a TEXT column, and
a text comparison against 'Cancelled' is exact. A database from the first release still
has the original `Status INTEGER` column, and there SQLite applies NUMERIC affinity to
the text literal: 'Cancelled' becomes 0, so an order whose status was written by that old
version as the ordinal 3 is "not cancelled" and gets marked paid with a cash payment it
never received — a fabricated entry in the very shift report the backfill feeds. The
legacy ordinal of Cancelled is 3, so it is listed next to the name and the CAST removes
the affinity from the comparison. Status members are never renamed, so both literals
stay stable; an order row of an app that no longer exists can still only be one of the
four names or the four ordinals.

## ExecuteAsync

```csharp
await SqliteSchemaHelper.ExecuteAsync(db,
```

One synthetic row per backfilled order, so the ledger closes over history and no order
reads as "paid by an unrecorded cash payment". lower(hex(randomblob(16))) is the 'N'
Guid format, which is the one new Guid(string) accepts for a key read back later. The
NOT IN guard keeps the statement idempotent: a re-run after a partial failure must not
add a second payment to the same order.

## Migration007_Refunds

```csharp
internal sealed class Migration007_Refunds : ISchemaMigration
```

Version 7 — refunds in the payment ledger: `OrderPayments.IsRefund` and
`OrderPayments.Note`.
Both columns are additions with a literal default, so each statement is O(1): SQLite's
ADD COLUMN never rewrites the table when the new column is constant-defaulted, which matters on
a device whose whole database lives in a phone's flash.
WHY there is no backfill, unlike version 6's PaidKopecks: every row that exists is a collection,
so `IsRefund = 0` is already the correct value and a second UPDATE pass would be pure
cost. Version 6 needed one because a NOT NULL column with no default would have turned every
pre-existing open order into a debtor; here the default IS the truth. (A converter would have
been the other way to be safe — see the EF model, which deliberately has none, following
`DraftOrder.IsActiveCart`.)
`Note` is nullable TEXT: a payment has no reason to give, and a refund always has one.

## Migration008_CashReconciliation

```csharp
internal sealed class Migration008_CashReconciliation : ISchemaMigration
```

Version 8 — the end-of-shift cash count: `Shifts.CountedCashKopecks`,
`Shifts.ExpectedCashKopecks`, `Shifts.ReconciledAt` and
`Shifts.CashDiscrepancyReason`.
ALL FOUR ARE NULLABLE WITH NO DEFAULT AND NO BACKFILL, and that is the whole point. A default
would fabricate a count for every historical shift — the app would then claim that shifts nobody
ever counted had been counted at exactly 0 ₽, which is a false audit record on exactly the rows
an auditor looks at first. A NOT NULL column would do worse: SQLite cannot add one to a table
with rows in it. So every shift that predates this migration keeps NULL, which the domain reads
back as "never counted" — distinct from a real count of 0, which is missing money and the one
case the feature exists to catch.
There is deliberately no fifth column for the discrepancy. It is
`Counted − Expected`, and storing it would be a third copy of an arithmetic fact that
`CashReconciliation.DiscrepancyKopecks` already derives from the two that ARE stored.
Each statement is a bare ADD COLUMN with no DEFAULT, so it is O(1): SQLite never rewrites the
table for a nullable column, which matters on a device whose whole database lives in a phone's
flash.

## Migration009_CashMovements

```csharp
internal sealed class Migration009_CashMovements : ISchemaMigration
```

Version 9 — `CashMovements`: the change put into the drawer and the cash taken out of it,
apart from anything that arrived through an order.
**AN EMPTY TABLE, AND NO BACKFILL — WHICH IS THE ONLY HONEST ANSWER.** The three columns that
would carry a history (shift, kind, amount) cannot be reconstructed for a shift that has already
closed: the opening float is not derivable from the orders (that is the whole gap this table
fills), and an incassation leaves no trace anywhere else. Any value written here would be a
figure nobody ever stated, sitting in the one table an auditor reads expecting exactly that. An
empty table reads as "this terminal did not record drawer movements before version 9", which is
true.
The consequence is stated so it is not rediscovered as a bug: a shift closed BEFORE this
migration reconciles against payments only, exactly as it always did. A shift opened after it
includes the float in what the drawer is expected to hold, so the two kinds of shift in the same
report differ by whether the operator recorded the change — not by any migration artefact.
`AmountKopecks` is NOT NULL with no default and holds a magnitude only; direction lives in
`Kind` and in `ReversesMovementId`. `Kind` is TEXT, matching how
`OrderPayment.Method` is stored, so the ledger is legible when opened in SQLite rather than
being ordinals only this build understands.
`ReversesMovementId` gets no FOREIGN KEY, deliberately. It points at a row in this same
table, and the one thing the app never does is delete a movement — so a constraint here would
only ever fire on a hand-edited database, where a cascade would take a second row with it and a
plain reference would turn a stray id into a write failure with no explanation. The link is
checked in `CashLedgerService`, which can say what is wrong with it.
The index is created explicitly because `SqliteSchemaHelper` parity is enforced by a
test that reads columns, not indexes: without this statement an upgraded terminal would have the
table with no index while a fresh one got one from EnsureCreated, and that divergence is
invisible until the day the drawer is large enough to notice.

## Migration010_CombosAndOrderFields

```csharp
internal sealed class Migration010_CombosAndOrderFields : ISchemaMigration
```

Version 10 — bundles and the new order-level facts: the `Combos`, `ComboComponents`
and `OrderItemComponents` tables, `Orders.OrderType`, `Orders.CustomerPhone`,
`Orders.RequestedAt` and `OrderItems.ListPriceKopecks`.
**THE THREE NEW TABLES ARE CREATED EMPTY, AND THAT IS THE ONLY HONEST BACKFILL.** Nothing here
can be reconstructed for a sale that already happened: the composition of a line was never stored,
and inventing one would put a fictional list of dishes into the exact table a fiscal receipt is
printed from. An empty table reads as "this terminal did not record compositions before version
10", which is true. The same reasoning made `CashMovements` (version 9) empty: a reconstructed
history is worse than an acknowledged gap, because nobody can tell afterwards which parts of it
were made up.
**ListPriceKopecks = PriceKopecks**, for every existing line, and the guard is the point. Until
this column existed the stored price WAS the allowed price — nothing could change one without
changing the other — so copying one onto the other states a fact, while leaving 0 behind would
make every historical line look like a 100% discount in the shift report's discount section. Old
orders can therefore never show a phantom discount.
**OrderType defaults to 'CounterService' and needs no UPDATE pass.** A NOT NULL column with a
literal default IS the backfill, the same argument version 7 makes for IsRefund. It is an
assumption rather than a fact — nobody recorded whether an old order was eaten here or taken away —
but it is the conservative one and it is safe in the only way an assumption about a phone can be:
those orders have no phone to lose and no promised time to mis-sort.
Every index the EF model declares is created explicitly, because the model/migration parity test
reads COLUMNS and not indexes. Without these statements an upgraded terminal would have the tables
with no index while a fresh one got them from EnsureCreated, and that divergence is invisible
until the day the catalogue is large enough to notice.

## KEY

```csharp
"FOREIGN KEY (SubstituteProductId) REFERENCES Products(Id)",
```

No ON DELETE clause on the substitute, because EF maps this optional relationship as
ClientSetNull (database NO ACTION, the client nulls the reference) and the constraint
here has to say the same thing a fresh install's DDL says.

## ExecuteAsync

```csharp
await SqliteSchemaHelper.ExecuteAsync(db,
```

4. The one backfill there is. The literal DEFAULT 0 kept the statement above O(1) — SQLite
does not rewrite the table for a constant-defaulted column — so the rows still carry 0
and this is where they learn their list price. WHERE ListPriceKopecks = 0 makes a re-run
after a partial failure harmless, and it cannot touch a line somebody has already
reviewed: a reviewed line has a non-zero list price, which is the whole signal.

## ExecuteAsync

```csharp
await SqliteSchemaHelper.ExecuteAsync(db,
```

5. The indexes the EF model declares. An index on a foreign key here is not decoration:
ComboId is how a bundle's composition is read, ProductId is how an 86 report finds the
slots that use a dish, OrderItemId is how a receipt is reprinted, and RequestedAt is
what the schedule section reads.

## ExecuteAsync

```csharp
await SqliteSchemaHelper.ExecuteAsync(db,
```

EF indexes every foreign key by convention, so the model has one on SubstituteProductId too.
This statement is not here because substitute lookups are hot — they are not — but because a
fresh install would get that index from EnsureCreated and an upgraded one would not, and the
parity test reads columns rather than indexes, so nothing would ever report the difference.

## Migration011_DraftComboComponents

```csharp
internal sealed class Migration011_DraftComboComponents : ISchemaMigration
```

Version 11 — the parked cart gets what the order got in version 10:
`DraftOrders.OrderType`, `DraftOrders.CustomerPhone`, `DraftOrders.RequestedAt` and
the `DraftOrderItemComponents` table.
**WHY A VERSION OF ITS OWN AND NOT PART OF 10.** None of these columns depends on a bundle
table: a café that never sells a bundle still needs its parked carts to keep the phone and the
requested time the operator already entered. Keeping the mirror in its own version means the two
sides can be read apart — version 10 is what a fiscal document is built from, version 11 is a
crash-safety net — and an operator who rolls back to a build without bundles keeps working carts
instead of losing them.
**THE REASON A PARKED CART KEEPS THE PHONE AT ALL, WHEN THE ORDER DROPS IT FOR COUNTER SERVICE.**
152-ФЗ ст. 6(1)(5) allows a phone to be processed only where it is needed to perform the contract,
and a parked counter-service order does not need one: it is dropped when the checkout writes the
order, exactly as `Migration010_CombosAndOrderFields` states. A draft keeps it only
because the operator is going to come back to this cart and finish it — losing a number they
already typed for a takeaway would make them type it again, and the second time it is typed it is
the one that is wrong. The data reaches no fiscal document until the cart becomes an order, and
that is where the decision is applied.
Each statement is a bare ADD COLUMN with a literal default or NULL, so every one of them is O(1):
SQLite never rewrites the table for such a column, which matters on a device whose whole database
lives in a phone's flash.

## Migration012_ComboOwnPrice

```csharp
internal sealed class Migration012_ComboOwnPrice : ISchemaMigration
```

Version 12 — a bundle states its own price: `Combos.PriceKopecks INTEGER NOT NULL`.
**WHY A BUNDLE HAS A PRICE OF ITS OWN AT ALL.** Until now it had none: the price was the sum of
its slots, so raising the price of a dish silently repriced every bundle containing it and nobody
decided anything. The reversal is deliberate (docs/PLAN-combos-order-details.md §1.3) — in Oracle
Simphony the price set in the item definition, not following the parts, is the documented
alternative to "Add Side Prices To Meal Price" — and the reason is ownership: accounting decides
the price and the discount, the till records it. A program that computes the price of a meal has
taken that decision away.
**THE BACKFILL IS THE COMPONENT SUM, AND THAT IS A FACT, NOT A GUESS.** Every existing bundle
was in fact charging exactly that: the old model made the sum the price, so the sum IS the price
these bundles have been selling at. Writing anything else would either reprice the catalogue on the
day of the upgrade (0 — every bundle suddenly free, which is money nobody authorised) or invent a
number from nothing. The sum can legitimately be 0 for a bundle whose slots are all free; that is
not corrected here on purpose, because the next save of that bundle is where a human decides its
price, and until then the record matches what it charged.
`COALESCE(cc.ComponentPriceKopecks, p.PriceKopecks)` is the same three-state rule the code
applies in `ComboPricing.ResolveUnitKopecks`: a NULL slot price means "the dish's own price",
and 0 means free. `× cc.QuantityPerUnit` is there because the multiplicity lives in the slot
and not in the line quantity. The LEFT JOIN, unlike Migration010's, is deliberate: a slot whose dish
row is missing cannot be priced, and an INNER JOIN would quietly drop it from the sum and
understate the bundle rather than admitting the gap — the FK makes that impossible from the app, but
a hand-edited database can still produce it and a smaller number would be the worse answer.
**NO INDEX IS CREATED HERE, AND THAT IS CORRECT RATHER THAN AN OMISSION.** The EF model declares
no index on `Combos` at all (see `AppDbContext.ConfigureCatalog`), and the parity test
reads columns through `PRAGMA table_info` and never creates a missing index, so an index
invented here would exist on upgraded terminals only and nowhere in the model. The rule that matters
— create every index the model declares, explicitly — has nothing to do for this version.
The column is added with a literal DEFAULT 0, which is O(1) on SQLite (no table rewrite), and the
UPDATE is the backfill. It is guarded by `WHERE PriceKopecks = 0` so a re-run after a partial
failure is harmless and cannot touch a bundle somebody has already re-priced: a bundle with a price
of its own is never zero, because `SaveComboAsync` refuses it.

## Migration013_OrderSeenAt

```csharp
internal sealed class Migration013_OrderSeenAt : ISchemaMigration
```

**WHY A COLUMN, WHEN `Orders.ReadyAt` ALREADY EXISTS.** The question the dot answers is
"has anyone seen this since it became ready", and that needs BOTH moments. `ReadyAt` is already
recorded when the status moves to `Models.OrderStatus.Ready`; without a second
timestamp there is nothing to compare it against and the only honest answer would be to show the dot
to everyone forever. Comparing two stored instants is what makes it decidable.
**WHY STORED RATHER THAN HELD IN MEMORY.** A dot that forgets itself on restart is worse than no
dot: the operator learns to ignore it, and then a genuinely unseen ready order looks identical to a
seen one. The board reloads itself on an auto-refresh tick (see `AutoRefreshSeconds`), so an
in-memory flag would also be lost on every rebuild of the row list, not merely on app restart.
**NULL IS THE NORMAL FIRST STATE AND MEANS "NEVER LOOKED", NOT "UNKNOWN".** An order that has
just been created and is not ready carries no dot either way, so there is no need to write a
sentinel row per order at creation. A NULL on a ready order means the operator has not opened it
since it became ready — which is exactly the state that should draw attention.
**NO BACKFILL, AND THAT IS CORRECT.** On upgrade every existing order is treated as unseen. That
is the right default: the alternative — backfilling `SeenAt = CreatedAt` to suppress the
dots — would invent a claim that somebody looked at orders nobody has looked at since the upgrade,
and would silence the first genuinely-useful signal the board offers.
**NO INDEX, FOR THE REASON Migration012 STATES.** The EF model declares no index on
`Orders` for this column, and the parity test reads columns through `PRAGMA table_info`
rather than creating what it finds missing. An index invented here would exist on upgraded
terminals only and nowhere in the model, which is worse than none.
The name has no `Utc` suffix, deliberately: every other timestamp on `Orders` is
`CreatedAt` / `ReadyAt` / `RequestedAt` and stores UTC without saying so, and a
column named `SeenAtUtc` would have needed an explicit `HasColumnName` to stop EF looking
for a `SeenAt` that the migration had not created.
Added as a nullable TEXT column with no DEFAULT, so SQLite takes the O(1) path that adds no table
rewrite and writes no value for existing rows.

## SchemaMigrator

```csharp
public sealed class SchemaMigrator(
```

Applies ordered schema migrations to the local SQLite database.
Replaces the previous combination of EnsureCreated() + ad-hoc ALTER TABLE statements
and the EF migrations folder that was never applied and referenced deleted models.

## ExecuteSqlRawAsync

```csharp
#pragma warning disable EF1002 // interpolated path into ExecuteSqlRawAsync; see above
```

VACUUM INTO produces a consistent snapshot while the app keeps running.
EF1002 is suppressed deliberately, not ignored. ExecuteSqlInterpolatedAsync would
bind the path as a parameter, and SQLite's VACUUM INTO requires a string *literal* —
the parameterised form is a syntax error. The value is escaped instead: EscapeLiteral
doubles single quotes, which is the complete escaping for a SQLite string literal, and
the path is not user input — it is Path.Combine(BackupDirectory, <generated name>),
where BackupDirectory comes from FileSystem.AppDataDirectory. The analyzer cannot see
either fact, so it warns on every build and the warning had become background noise.

## shift

```csharp
var shift = await orders.GetLatestShiftAsync(cancellationToken);
```

The shift the report is about: the open one, or the last one closed when nothing is open.
It used to be GetOrCreateActiveShiftAsync, which both created a shift and could not fail.
Now that a shift is opened deliberately there is a legitimate state with no open shift —
a terminal between shifts — and an export that threw there would lose the products and
sales CSVs too, because they were already built by then. Falling back to the most recent
closed shift keeps the archive useful at exactly the moment somebody is most likely to
take one.

## struct

```csharp
public readonly record struct PendingCashCount(long CountedKopecks, long ExpectedAgainstKopecks);
```

ONE TYPE FOR TWO NUMBERS, on purpose. A count cannot be judged still true without the figure it
was taken against, and a figure on its own is nothing the operator ever typed — so neither half
is representable here without the other, and a caller cannot store a pre-fill that forgets which
drawer it belongs to.

## CashCountPrefill

```csharp
public static class CashCountPrefill
```

In the core, free of Maui, of dialogs and of `ObservableCollection`, because this is a rule
about the drawer and not about a dialog — and because it is the rule that was only ever checked
by hand.
THE RULE: a previous count is offered back only while the drawer still holds what it held when it
was counted. Otherwise the live expectation is offered, which is what the prompt's own text
states out loud ("По учёту 5 580,00") and therefore cannot contradict.
The case this exists for was seen on a device: after a close was refused because orders were
still open, two refunds were taken while the operator went and closed those orders. The dialog
went on offering the earlier 6 660,00 in a field labelled «по учёту 5 580,00», and one tap on
«Закрыть смену» would have stored a 1 080 discrepancy that nobody ever counted — written to the
shift as fact, on the one figure the reconciliation exists to get right. Comparing the stored
`PendingCashCount.ExpectedAgainstKopecks` against the live figure closes that, and
returning the live figure is the only honest answer in the case where the two have parted company:
the operator's earlier count is still a true statement about money that is no longer in the till.

## Resolve

```csharp
public static long Resolve(PendingCashCount? previous, long expectedKopecks) =>
```

The figure in kopecks to pre-fill: the previous count when it was counted against the drawer
as it stands now, and <paramref name="expectedKopecks"/> in every other case — no previous
count at all, or a drawer that moved since.
<param name="previous">The count already collected in this close attempt, if there is one.</param>
<param name="expectedKopecks">The drawer's figure right now, in kopecks.</param>

## CatalogAction

```csharp
public sealed record CatalogAction(string Key, string Title, bool IsDestructive = false);
```

The data behind the catalogue row overflow menu. Deliberately the only part of that feature
left in the core: the interface that presents it (ICatalogActionSheet) is a MAUI platform gap
and now lives with the other platform abstractions in Services/Abstractions/PlatformServices.cs.

## CatalogActions

```csharp
public static class CatalogActions
```

This is a deliberate testability trade, and the reason it stays in the core rather than moving
down to the app with `ICatalogActionSheet`: keeping the available set of actions, the
wording and the destructive flag as plain data is what lets `Tests/CatalogActionsTests.cs`
assert on them from a plain `net10.0` test project that references only `CafePos.Core`.
The accepted cost is that the Russian operator-facing wording ("Редактировать", "Удалить",
"Снять с продажи") lives in the platform-independent domain assembly. Moving that wording to the
app layer would be the purer layering, but it would drag the action set with it and mean
rewriting `CatalogActionsTests` — and the tests would then need a project reference to the
MAUI app project, which is multi-targeted and not referencable from that test host. The trade is
worth it; it is recorded here so the next reader does not "fix" it.

## CatalogService

```csharp
public sealed partial class CatalogService
```

The class is already partial and was already 416 lines with 24 methods over five aggregates.
Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
to the primary constructor or to DI. Only this part declares the constructor and the
interface - the others repeat neither.

## CatalogService

```csharp
public sealed partial class CatalogService
```

The class is already partial and was already 416 lines with 24 methods over five aggregates.
Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
to the primary constructor or to DI. Only this part declares the constructor and the
interface - the others repeat neither.

## DeleteIngredientAsync

```csharp
public async Task DeleteIngredientAsync(Guid id, CancellationToken cancellationToken = default)
```

Hard delete, with one exception that is not cosmetic: an ingredient that has ever been
written off cannot be deleted.
Ingredients have no IsDeleted flag (unlike products), and StockMovements.IngredientId is
ON DELETE CASCADE — so deleting one physically destroys its stock journal. A cancellation
with StockDisposition.ReturnToStock reverses the write-off by reading that journal, which
means a deleted ingredient leaves nothing to invert while the operator is being told the
stock went back. That is a lie about the shelf, and a lie nobody could audit afterwards: the
rows that would have shown it are the rows that were deleted. Switching the ingredient off
(IsAvailable = false) hides it from the catalogue and keeps the journal intact, which is why
that is the action the message points at.

## CatalogService

```csharp
public sealed partial class CatalogService
```

The class is already partial and was already 416 lines with 24 methods over five aggregates.
Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
to the primary constructor or to DI. Only this part declares the constructor and the
interface - the others repeat neither.

## CatalogService

```csharp
public sealed partial class CatalogService
```

The class is already partial and was already 416 lines with 24 methods over five aggregates.
Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
to the primary constructor or to DI. Only this part declares the constructor and the
interface - the others repeat neither.

## Add

```csharp
db.ProductVariants.Add(new ProductVariant
```

Add through the DbSet, not through the loaded navigation: an entity pushed into
the Variants collection of a tracked product is tracked as Modified (its key is
already set, so EF cannot tell it is new), and EF then issues an UPDATE that
matches no row → DbUpdateConcurrencyException.

## CatalogService

```csharp
public sealed partial class CatalogService
```

The class is already partial and was already 416 lines with 24 methods over five aggregates.
Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
to the primary constructor or to DI. Only this part declares the constructor and the
interface - the others repeat neither.

## foreach

```csharp
foreach (var group in groups)
```

AsNoTracking means EF does not fix up the inverse navigation, so every
ModifierOption.ModifierGroup stays null and the catalogue row renders an empty
"Группа: " label. GetProductsAsync does not hit this because it loads the group
from the product side. Set the back-reference explicitly — it is a pure in-memory
assignment on already-materialised entities.

## ContactDataService

```csharp
public sealed class ContactDataService(
```

**ERASE THE VALUE, NEVER THE ROW.** 152-ФЗ ст. 5(7) is about the personal data, not about the
sale: the receipt is a fiscal document and deleting orders to satisfy a retention rule would be
trading one legal problem for a worse one. So the column is nulled in place and the order — its
numbers, its lines, its payment — is untouched. What leaves the database is exactly the thing that
identifies a person.
**IT IS THE NUMBER OF ROWS THAT COUNTS, WHICH IS WHY THE WRITE IS BULK.** CoAP 13.11 ч.12 prices
unlawful disclosure at 3–5 million ₽ for 1 000–10 000 subjects and counts rows, so a hundred phones
cleared one at a time over a year is a hundred rows an inspection could have found. The statement
therefore updates every eligible row in one pass, inside the database, instead of loading entities
and saving them one by one.

## DefaultRetention

```csharp
public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);
```

How long a phone is kept. Ninety days is long enough to settle a complaint about a takeaway
("nobody called me back") and short enough that the data is gone long before anyone asks about
it. It is a default and not a hard-coded policy: the shift close passes it in, and the interface
takes any threshold, so a café with its own retention period can say so without this changing.

## candidates

```csharp
var candidates = await db.Orders.AsNoTracking()
```

The candidates are read and dated IN MEMORY on purpose, and this is the same reason
GetActiveOrdersAsync sorts there: SQLite cannot translate a comparison or an ORDER BY over a
DateTimeOffset and throws NotSupportedException rather than doing something approximate. The
set is small by construction — it is only the closed orders that still HAVE a phone, and
this service shrinks it once a day — so the projection costs less than any workaround that
would put a timestamp comparison back into SQL.
Closed AND carrying a number worth erasing, in the query rather than after it: the sweep
then reads and rewrites only the rows there is work to do. An order still being cooked keeps
its number — the customer has not been served yet, so the purpose has not ended.

## purged

```csharp
var purged = await db.Orders
```

One statement, and no entities: the whole point of a bulk erase is that the data is not
loaded into the process at all. ExecuteUpdate bypasses the change tracker, which is also what
keeps this cheap on a database that lives in a phone's flash.

## ClosedAt

```csharp
private static DateTimeOffset ClosedAt(DateTimeOffset? completedAt, DateTimeOffset? cancelledAt) =>
```

When the order stopped being live: the completion for a sale, the cancellation for a void. A
row with neither is a closed order whose terminal never wrote the timestamp — the cancellation
path always writes CancelledAt and the status path always writes CompletedAt, so this is a
hand-edited or interrupted row, and falling back to the oldest possible moment errs towards
purging a number rather than towards keeping one forever.

## ContactPhoneRule

```csharp
internal static class ContactPhoneRule
```

**WHY THIS IS A SEPARATE TYPE AND NOT A SECOND CALL SITE.** The rule is not "parse a phone": it is
three decisions in a fixed order — is this order allowed to hold a number at all, is what was typed a
number, and is that number one this till will store. That sequence existed as a private method inside
`CheckoutService`, which made it unreachable from the other door into the same column: adding a
phone to an order after it was paid for. Copying it there would have produced two implementations of
one legal contour, and the copy is exactly the one nobody writes a test for.
**THE ORDER OF THE CHECKS IS THE POINT.** Fulfilment mode is tested FIRST, before anything is
parsed. 152-ФЗ ст. 6(1)(5) allows a phone only where it is needed for the contract, and a
counter-service customer is on the premises, so the number is discarded without being read. A till
that validates text it is about to throw away stops a sale for nothing, which is why
`CheckoutService`'s own comment records that choice too.

## ForStorage

```csharp
public static string? ForStorage(string? typed, OrderType orderType)
```

Normalises a typed number for storage, or returns `null` when the order may not hold one.
<param name="typed">Whatever was typed or pasted.</param>
<param name="orderType">
The fulfilment mode the number will be stored under. A counter-service order yields `null`
regardless of what was typed.
</param>
<exception cref="ValidationFailureException">
The text is not a number, or is a number this till will not store. Never thrown for counter
service, because that number is being discarded rather than stored.
</exception>

## if

```csharp
if (!PhoneNumber.IsValid(normalized))
```

A number that parsed but is not a usable length is refused rather than stored: a fiscal
receipt with "+71234" on it is a worse outcome than an order that could not be taken, and the
message says what is wrong instead of leaving the operator to guess.

## ThenInclude

```csharp
.ThenInclude(item => item.Components)
```

A bundle's slots, or the restored line prints with nothing under it AND loses its merge
signature: the cart merges on OrderLineKey, which carries the composition. A restored
bundle would then split from the identical bundle the cashier adds next, and the cart
would show the same bundle twice at the same price. See MenuViewModel.RestoreDraftAsync.

## replacement

```csharp
var replacement = lines.Select(line => ToItem(line, draft.Id)).ToList();
```

The old lines go away and a fresh set comes back. Both halves have to be stated
explicitly, and the reason is EF Core's change detection rather than this code:
DraftOrderItem.Id is client-assigned (Guid.NewGuid in ToItem), so EF cannot tell a
brand-new item from an existing one by its key alone. When new items were only
*assigned* to draft.Items, change detection tracked them as Modified against the
Unchanged draft — never Added. The batch then ran
DELETE FROM "DraftOrderItems"   (the old lines)
UPDATE "DraftOrderItems" SET .. (the new lines, by the deleted keys)
and the UPDATE matched 0 rows, so every cart autosave after the first ended in
DbUpdateConcurrencyException and the draft was never written. The first save worked
only because the draft itself was Added, which cascades Added to its dependents.
AddRange states the intent outright, so the batch is DELETE-then-INSERT.

## ToLine

```csharp
private static CheckoutLine ToLine(DraftOrderItem item) => new(
```

A parked line as the cart reads it. The slots come last and in `SortOrder`, because the
merge key is order-sensitive: two bundles whose slots arrived in a different sequence are
different compositions as far as `OrderLineKey` is concerned.

## order

```csharp
var order = 0;
```

The slots are what make a bundle restorable AS a bundle. Left out, a parked bundle came back
as a bare line: nothing printed under it, and its merge key no longer matched the identical
bundle the cashier added next, so the cart grew a second copy instead of a quantity of 2.
SortOrder is the slot's index on the cart, which is what CheckoutComponent does not carry.

## IContactDataService

```csharp
public interface IContactDataService
```

Retention of customer contact data: 152-ФЗ ст. 5(7) requires personal data to be destroyed once
the purpose for collecting it is met, and a phone number on a takeaway order has had its purpose
the moment the order is handed over.

## PurgeAsync

```csharp
Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
```

Called from the shift close, which is the schedule this app already has. A nightly job or a
background timer would be a second thing that has to run, has to be rescheduled when the phone
restarts, and can be missed silently — and a retention promise that depends on a scheduler
firing is not a promise. Once a day is also the coarsest interval that could honestly satisfy
90 days, and it comes free with something an operator already does at the end of every working
day.
ONLY CLOSED ORDERS. A phone on an order still being cooked is still the only way to reach the
customer about it, and the purpose has not ended yet. Clearing it would be the wrong kind of
thorough.

## IOrderOperations

```csharp
public interface IOrderOperations :
```

The aggregate port: read an order, move it, take its money. Most order screens want exactly this
and nothing else, and giving each of them three constructor parameters
(`IOrderQueries`, `IOrderCommands`, `IOrderPayments`) would say
the same thing more loudly.
What it deliberately EXCLUDES is the point of it. The orders board and the order details screen
used to hold the flat twenty-three-method `IOrderService`, which meant both could in principle
call `CloseShiftAsync` — reconciling a drawer from a screen that displays orders. Neither needs
a shift to do its job, so neither gets a shift.
The three verb-level ports stay public for a caller that needs exactly one — nothing above them
forces them together.

## IOrderPayments

```csharp
public interface IOrderPayments
```

Separate from `IOrderCommands` because the two fail differently. An order can advance
without money and money can move without the order advancing — a part-payment against an order
still cooking is the ordinary case, not an edge — so a port that folded them together would let
"record a payment" be written as if it implied the goods were handed over.

## AddPaymentAsync

```csharp
Task<Order> AddPaymentAsync(Guid orderId, decimal amount, PaymentMethod method, CancellationToken cancellationToken = default);
```

Records a payment against an order: adds the ledger row and moves Orders.PaidKopecks in one
save. <paramref name="amount"/> is rubles, clamped to the outstanding balance (a tendered
surplus is change, not revenue) and rejected when nothing is owed.

## RefundAsync

```csharp
Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default);
```

Returns money on a finished order: adds the refund row(s) and lowers Orders.PaidKopecks in one
save. Only for `OrderStatus.Completed` — money goes back after the goods left the
bar. <paramref name="amount"/> is rubles, clamped to what is still collected.
There is NO payment method parameter, and that is the point: the refund mirrors the order's
own payments, oldest first. A POS has two tills, and booking the refund under a method the
money never arrived in would leave one of them wrong at the count with nothing in the app to
say so.

## IOrderQueries

```csharp
public interface IOrderQueries
```

Split out of the former flat `IOrderService` because a screen that lists orders has no
business refunding one or closing the till, and an interface that offers both is an interface
that will eventually be used to do both.
Reads only. A caller that wants to change an order needs
`IOrderCommands`, `IOrderPayments` or `IShiftLedger`, and
the compiler — not a review — is what stops it.

## GetShiftOrderHistoryAsync

```csharp
Task<List<Order>> GetShiftOrderHistoryAsync(Guid shiftId, CancellationToken cancellationToken = default);
```

The shift report's on-screen history: the orders of the shift that are closed, i.e. Completed
AND Cancelled. Deliberately not `GetCompletedOrdersAsync` — cancelling flips a
Completed order to Cancelled, so a manager would watch a voided sale disappear from the
history instead of showing up marked as voided, and the one thing they most need to see is
exactly the sale that went wrong.

## IOrderReporting

```csharp
public interface IOrderReporting
```

The narrowest and most misused port before the split. `ShiftAnalyticsViewModel` calls two
methods on it and held an interface with twenty-one others, one of which closed the till — so a
reporting screen carried the authority to reconcile a drawer it only ever displays.
It is the reporting side, so it is also where the definitions live:
`ShiftStats.ExpectedCashNow` and `ShiftStats.Reconciliation` are computed
once here rather than by each screen, because a report that recomputes the difference is a second
definition of the same figure.

## GetShiftStatsAsync

```csharp
Task<ShiftStats> GetShiftStatsAsync(Guid shiftId, CancellationToken cancellationToken = default);
```

All shift aggregates. Single source of truth for the shift report and analytics.
It also carries the ONE definition of "what is in the drawer right now"
(`ShiftStats.ExpectedCashNow`) and the frozen count comparison
(`ShiftStats.Reconciliation`). Presentation subtracts nothing: a view model that
recomputes the difference is a second definition, and a second definition is how a report
ends up netting one meaning of "принято" against another.

## GetProductAnalyticsAsync

```csharp
Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default);
```

The shift's product breakdown, one row per distinct product × modifier × variant, with the
section each dish currently belongs to. UNSORTED on purpose: the breakdown is ordered by
`CafePos.Core.Services.ProductAnalyticsProjection`, which is the one place the
order is defined — an `ORDER BY` here would be a second definition that silently wins
whenever a screen forgets to re-sort.

## GetDiscountedLinesAsync

```csharp
Task<List<DiscountedLine>> GetDiscountedLinesAsync(Guid shiftId, CancellationToken cancellationToken = default);
```

The shift's lines whose charged price differs from the price they were allowed to be sold at:
the "Скидки" section of the shift report.
This is where the price control becomes visible, and it is detection rather than prevention:
nothing is refused at the till, and a manager reads this after the shift. Ordered by order
number and then by the line's position in the order, so the section reads in the order the
sales happened rather than in whatever order the query returned.
Includes CANCELLED orders on purpose. A voided sale is where an overridden price is most worth
seeing, and it is absent from the revenue — which is exactly why every row carries its
`DiscountedLine.Status` and why the section must not be summed as if it were one
number.

## IOrderService

```csharp
public interface IOrderService :
```

**This is a composite, not a god interface.** It declares no methods of its own — it is only the
union of five ports that each say one thing:
<list type="bullet">
<item><description>`IOrderQueries` — listing and opening orders.</description></item>
<item><description>`IOrderCommands` — status, lines, contact details.</description></item>
<item><description>`IOrderPayments` — money in and out of one order.</description></item>
<item><description>`IShiftLedger` — the shift and the drawer.</description></item>
<item><description>`IOrderReporting` — the shift read as numbers.</description></item>
</list>
It was one flat interface of twenty-three methods before the split, which is how a reporting
screen came to hold the authority to close the till. `OrderService` implements all
five — it is one class and one DbContext, and nothing here duplicates behaviour — but a caller
should ask for the port it needs rather than this: `IOrderOperations` for order
screens, `IShiftLedger` for the till, `IOrderReporting` for the numbers.
Who legitimately needs the whole thing: the shift report, which reads aggregates AND closes the
shift AND voids an order that turns out to be wrong; and the test suite, which exercises
scenarios that cross the same lines on purpose.

## ledger

```csharp
var ledger = await CashLedger.ReadAsync(db, shiftId, cancellationToken);
```

"Принято оплат" — what the till recorded, next to the revenue figure above. Exported on
purpose: the shift report is what a manager takes to the cash count, and without these
lines the drawer total cannot be reconciled against the day on paper. The wording and the
GROSS meaning are deliberately untouched: every existing reading of "Принято …" keeps
meaning "what came in".

## AppendLine

```csharp
builder.AppendLine(Csv.Join("Возвращено наличными", Money.FromKopecks(payments.RefundsCashKopecks).ToString("F2")));
```

What went back out, by the method it left in. Reported separately from "Принято" rather
than subtracted into it: the gross figure is what the till took, and quietly lowering it
would make a day with refunds look like a day that took less.

## AppendLine

```csharp
builder.AppendLine(Csv.Join("Внесено размена", Money.FromKopecks(ledger.FloatKopecks).ToString("F2")));
```

The two figures that are not orders. Printed as their own lines, ABOVE the total, because a
"Итого наличными в кассе" that does not balance against the line above it is the exact
situation an export is taken to the till to resolve — and a drawer with change in it has
never been explainable from "Принято наличными" alone.

## shift

```csharp
var shift = await db.Shifts.AsNoTracking()
```

The count itself, printed last because it is the manager's own answer to the drawer line
above. The shift row is loaded here for the first time: the export used to read orders
only, so it had nothing to say about the money after it left the drawer.

## AppendReconciliation

```csharp
private static void AppendReconciliation(
```

The cash count block. Two rules, both of them about not lying:
<list type="bullet">
<item>Every "expected" figure carries the MOMENT it was taken at. A bare "Ожидалось" line is
the ambiguity the feature exists to remove — read a week later it is a claim about some
unspecified drawer, and an auditor cannot use it.</item>
<item>A shift that was never counted says exactly that, in one line, and prints no numbers at
all. A historical export must never be readable as "the drawer came out at 0" — that is a
fabricated count on precisely the rows somebody checks first.</item>
</list>

## drift

```csharp
var drift = reconciliation.ExpectedKopecks - expectedNowKopecks;
```

Money that left the drawer AFTER the count was recorded — a refund against a closed shift,
which is allowed precisely because the cash physically belonged to that drawer. Printed
only when there is any: at zero the pair would repeat the drawer line above in other
words. The value can only be non-negative for a counted shift, since the only thing that
can lower a closed shift's live cash figure is a refund.
