# Общее: телефон, время, коллекции

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## CartStepDown

```csharp
public sealed record CartStepDown(
```

What stepping a cart line does, and what — if anything — the cashier can take back.
<param name="QuantityAfter">What the line's own quantity becomes.</param>
<param name="RemovesLine">True when the line leaves the cart entirely and should be taken out of it.</param>
<param name="Announce">
What the message strip says, or `null` for silence. Silence means the ViewModel must not touch
the message property at all: assigning an empty string would retire a pending undo, because that is
what that property's setter does.
</param>
<param name="UndoQuantity">
The quantity to restore the line to if the removal is undone, or `null` when no undo is armed.
</param>

This is the one part of the cart that is a <i>decision</i> rather than a mutation, and it was
written inline inside `MenuViewModel` where nothing could ask it a question. The mutations
stay on the ViewModel — they touch an `ObservableCollection` bound in XAML — but the rule
below is pure and is now testable.
**ONLY THE STEP THAT REMOVES THE LINE ARMS AN UNDO.** Stepping 3 down to 2 is not a mistake worth
a control: the cashier can step back up. A line leaving the cart is the one edit on this screen with
no visible way back, and NN/g's finding that users «accidentally added the same item to their cart
multiple times» is that same failure seen from the other side. So an unintended edit is corrected
by an affordance, not by another tap on the line that is no longer there.

## From

```csharp
public static CartStepDown From(int quantityBefore, string productName)
```

Steps a line's quantity down by one.
<param name="quantityBefore">
The quantity as it stands NOW, before the decrement. Passed in rather than read from the line so
the undo can restore the line as it was: the ViewModel decrements the line before removing it,
so an undo armed from the value afterwards would bring back a row reading «0».
</param>
<param name="productName">The line's name, for the message.</param>

## RestoreIndex

```csharp
public static int RestoreIndex(int index, int count)
```

**A CART READ TOP TO BOTTOM IS A SEQUENCE.** A cashier who removes the third line and undoes
it expects the third line back, not the last one — appending silently reorders a cart they have
already read.
The index can be out of range if the cart changed underneath in the meantime: a draft restored,
a line added and removed again. Appending is the honest fallback for that, and it is far better
than throwing from a tap on «Отменить».

## ClockTime

```csharp
public static class ClockTime
```

**WHY THIS EXISTS AT ALL: `TimeSpan.ToString("HH:mm")` THROWS.** A `TimeSpan`
has its own, much smaller set of custom format specifiers than a `DateTime`: they are
`d h m s f F`, all lowercase, and a colon has to be escaped. `"HH:mm"` therefore raises
`FormatException` — measured, not inferred:
<code>
"HH:mm"  =&gt; FormatException: Input string was not in a correct format.
"hh:mm"  =&gt; FormatException: Input string was not in a correct format.
"hh\:mm" =&gt; 14:20
</code>
On a `DateTimeOffset` the same pattern is fine, which is what makes the mistake so easy
to make: half this app's times are timestamps and half are durations, and the two accept different
format strings.
That one format string is not a typo — it was the whole of the crash reported as «выбор ко времени
вылетает». `TimePickerPopup.SetMode` formatted the chosen time with it to build the confirm
button's caption, on the UI thread, from an event handler, so the process died the instant the
operator chose anything other than «сейчас». Both routes reached it: pressing
«К выбранному времени», and moving the native dial (the dialog's own `onTimeSet` callback
assigns `TimePicker.Time`, which raises `PropertyChanged`, which called `SetMode`).
One method for both shapes rather than two call sites, so the escaping is decided once. It is here,
It is in Core rather than beside the sheet because the ORDER PROMISED TIME NEEDS IT, not the sheet:
`OrderPromise` describes an order's promise in the same words the sheet offers, and that is a
domain rule with no UI in it.
a rule a test can reach.

## Format

```csharp
public static string Format(TimeSpan timeOfDay) =>
```

Explicitly 24-hour, and not the current culture's short time pattern: a coffee shop's promise is
quoted back to the customer in the same words all day long, and a locale that renders «2:20
PM» beside a «22:20» on the order board produces exactly the ambiguity this sheet exists to
remove. The escaped colon is the load-bearing part — see the remarks above.

## Format

```csharp
public static string Format(DateTimeOffset moment) => Format(moment, TimeZoneInfo.Local);
```

The mirror of `Format(TimeSpan)`, and it needs no escaping — `DateTimeOffset`
really does accept «HH:mm». Both overloads exist so the two call sites read alike and nobody has
to remember which type they are holding.

## Format

```csharp
public static string Format(DateTimeOffset moment, TimeZoneInfo? zone) =>
```

Exists so a promise PLACED in one zone can be READ BACK in the same one. The default overload
converts with `DateTimeOffset.ToLocalTime`, which uses the machine's zone — fine in
production where there is only ever one zone, and wrong in a test where the ViewModel's clock is
pinned elsewhere: the message the operator reads back would be an hour out from the time they
chose.

## NowPlus

```csharp
public static TimeSpan NowPlus(int minutes, int gridMinutes) =>
```

ONE method because the seed and the presets must agree, and a preset landing on a time the seed's
grid could never reach would be a control that sets something the sheet cannot then show.

## FulfilmentSwitch

```csharp
public sealed record FulfilmentSwitch(
```

What switching an order between counter service and takeaway costs the operator.
<param name="OrderType">The type the order becomes.</param>
<param name="Changed">
False when `target` was already the order's type, so the caller can return early. Carried
explicitly rather than inferred from the other three fields: a silent move to takeaway looks
exactly like a no-op on those fields alone, and a toggle pressed twice MUST be inert.
</param>
<param name="DropsPhone">
True when a typed phone number is about to be destroyed. This is the signal to speak, and it is
deliberately NOT the same as `ClearsPhone`: the phone property is cleared on every move
to counter service, but only a move that loses a number is worth a sentence.
</param>
<param name="ClearsPhone">Whether `CustomerPhone` must be set to null.</param>
<param name="Announce">
The sentence to show, or `null` for silence. Silence means **do not touch the message
property at all** — assigning an empty string would still retire a pending undo, because that is
what `Message`'s setter does, and «Отменить» armed by a removal has to survive a tap on the
fulfilment button.
</param>

**THE ONLY NON-ROUTINE SWITCH ON THE CART SCREEN.** Moving to counter service deletes a phone
number the operator typed, and it deletes the row showing it in the same frame. That is not a
state anyone can read off a row afterwards, so it is the one place this screen earns a message —
and the message is worth spending only on the loss, never on the ordinary tap.
The decision is pure: (current, target, has phone) in, (what changes, what to say) out. The
ViewModel applies it and owns the notification. This is the third application of the same split as
`ProductAddFlow` and `BundlePlan`.

## Decide

```csharp
public static FulfilmentSwitch Decide(OrderType current, OrderType target, bool hasPhone)
```

Decides the consequences of moving an order to <paramref name="target"/>.
<param name="current">What the order is now.</param>
<param name="target">What it is becoming. Equal to <paramref name="current"/> means nothing changes.</param>
<param name="hasPhone">Whether a phone number has actually been typed — blank is not a number.</param>

## MenuFilter

```csharp
public static class MenuFilter
```

**THE SENTINEL KEYS BELONG HERE, NOT ON THE CHIP.** "Everything" and "bundles only" are not
categories — a bundle carries no `CategoryId` — so the strip needs two keys that no generated
category id can equal. They lived on `CategoryMenuItemViewModel`, which made the rule "is this
a category that still exists?" depend on a row type in the presentation layer, and made it
unreachable from a test.
The two sentinels are `Guid.Empty` and a literal ending in `c0`, chosen so neither
can collide with a generated Guid. A `Category.Id` is a real `Guid`, so
"all" is deliberately not a Guid a database could produce rather than being one that cannot be.

## AllKey

```csharp
public static readonly Guid AllKey = Guid.Empty;
```

Sort key of the «Все» chip. `Guid.Empty` and not a random Guid, so it is stable
across processes for the same reason as `CombosKey`. Real category ids are
generated Guids, so nothing the database hands out can collide with it.

## CombosKey

```csharp
public static readonly Guid CombosKey = Guid.Parse("00000000-0000-0000-0000-0000000000c0");
```

Sort key of the «Комбо» chip. A fixed literal, not a random Guid, so it is stable across
processes — the strip is rebuilt and diffed on every return to the tab, and a key that changed
per launch would make `SyncWith` treat the chip as a different item each time and lose the
selection highlight.

## Repair

```csharp
public static Guid Repair(Guid selectedKey, IReadOnlyCollection<Guid> knownKeys) =>
```

The key that is actually usable, given one that was selected earlier and the keys that exist now.
<param name="selectedKey">The key that was selected before the reload.</param>
<param name="knownKeys">Every key the strip now offers, including the two sentinels.</param>

**THE SELECTED CATEGORY MAY HAVE BEEN DELETED WHILE THE PAGE WAS CLOSED.** Shell re-runs the
load on every return to the tab, so a chip can name a category that no longer exists. Falling
back to «Все» is the only honest reading of that state: leaving the key pointing at a chip that
is not there would highlight nothing and filter by nothing, and the grid would look empty for
a reason the operator cannot see.
The two sentinels are always accepted, whether or not the caller built a chip for them, because
they stand for "no category" rather than for a row.

## Apply

```csharp
public static IReadOnlyList<Product> Apply(IEnumerable<Product> products, Category? category, int localHour)
```

The dishes on show: the selected category's, and only those inside their time window.
<param name="products">Everything loaded, before filtering.</param>
<param name="category">The chosen category, or `null` for everything.</param>
<param name="localHour">
The hour at the till, which decides the time window. Passed in because a dish served at 23:50 must
not be judged by the hour on the server.
</param>

**THE BUNDLES FILTER EMPTIES THE LIST RATHER THAN NARROWING IT.** A bundle is not a
`Product` and carries no category id, so every comparison against a category would be
true at once and the grid would show the whole menu behind a chip that promises bundles. An empty
grid with the bundle tiles on top of it is the honest answer.

## Money

```csharp
public static class Money
```

All monetary values are persisted as integer kopecks, because SQLite stores decimals as TEXT:
TEXT columns break SUM/ORDER BY/comparisons in SQL and are not supported by decimal aggregates.
The domain exposes decimals through `Money` so that callers keep using rubles.

## ObservableCollectionSync

```csharp
public static class ObservableCollectionSync
```

Updates an existing `ObservableCollection{T}` in place.
Calling Clear() + Add() for every refresh (as the app did) makes the list flicker and
resets the scroll position, which is very visible on the order board and the menu.
**SyncWith NEVER raises `System.Collections.Specialized.NotifyCollectionChangedAction.Replace`,
and that is the reason it exists in this shape.** Every list in the app that is fed from here is
bound through MAUI's BindableLayout, and of its five handlers Replace is the only one that does
not re-establish the correspondence between a collection item and a realised child: it indexes
into `layout.Children[e.OldStartingIndex]` and throws
`ArgumentOutOfRangeException` when the children are not there yet. Add, Move, Remove
and Reset all go through `e.Apply(...)` — `layout.Insert`, `layout.RemoveAt`,
`CreateChildren()` — and cannot produce that shape. So Replace is the canary rather than the
root cause, and removing the one place in the app that raises it protects all 21 BindableLayout
bindings in `Views` structurally: no registry, no XAML edits.
A row whose key and position both match is therefore refreshed by one of two means, never by
`SetItem`:
<list type="bullet">
<item>with a `refresh` delegate, the instance already in the collection is updated and the
collection raises NOTHING at all — the cheapest refresh there is, and the only one that needs a
row which can be written to;</item>
<item>without one, the row is re-created AT THE SAME INDEX (RemoveAt + Insert at that index, not
an append). That is the pair of actions BindableLayout applies through `layout.RemoveAt` /
`layout.Insert`, and it cannot leave a stale row behind whatever the row type is.</item>
</list>
The re-create branch is the fallback, not the second choice, because the app's rows cannot be
updated in place at all: `OrderRowViewModel` is a `Model { get; }` with computed
properties, so there is nothing on it to write. Forbidding the refresh without offering the
fallback would push the burden onto callers reusing an immutable instance, which is the one
thing that guarantees a stale row on screen. Re-creating at the same index rules staleness out by
construction: whatever is in the collection is what came from the source.
`object.Equals(object)` stays the cheap "nothing changed" check ahead of both. For
rows with value equality — records, and an `ObservableCollection&lt;string&gt;` compared by
value — it is true and no action is taken at all, which is exactly how those lists behaved before
and is deliberately preserved.

## public

```csharp
public static void SyncWith<T, TKey>(
```

Brings <paramref name="target"/> to the contents of <paramref name="source"/>, matching rows
by <paramref name="keySelector"/> and reporting only Add, Move, Remove and (through
<paramref name="refresh"/>) nothing at all. The collection instance itself is never replaced,
because the binding points at it.
<param name="refresh">
Called as `refresh(current, incoming)` for a row whose key and position both match but
whose value differs, and expected to write the incoming values onto the current instance.
Null (the default) means re-create the row at the same index instead — see the remarks on the
class for why that fallback exists and why the row is re-created rather than replaced.
</param>

## if

```csharp
if (!Equals(target[index], item))
```

A Move alone is not enough. Move raises one action and BindableLayout applies it as
removeAt + CreateItemView, so the child is rebuilt from the instance the collection
now holds — which is the OLD one. Without this branch the incoming instance is
discarded and the row silently shows the previous pass's values. It only bites when a
row moves UP (existingIndex > index); a row moving down arrives as an Insert above it
and is therefore recreated anyway, which is what made this look unreachable.
The refresh is applied AFTER the Move, at the destination index, because that is where
the stale instance now sits.

## RemoveAt

```csharp
target.RemoveAt(index);
```

Re-created AT THIS INDEX, never appended: an Add at the end would silently
reorder the list, and every row below this one would then be built against a
neighbour that moved. RemoveAt + Insert is what BindableLayout applies
internally, so the realised children follow the collection.

## OrderPromise

```csharp
public static class OrderPromise
```

**THIS WAS THREE PRIVATE METHODS ON A VIEWMODEL AND WAS NOT TESTABLE.** Placing a clock time on a
date, deciding whether the promise has already passed, and wording the message are all pure
functions of (what was picked, what time it is now) — and all three lived inside
`MenuViewModel`, where a test could not reach a single one of them. The time placement in
particular carries the rule that a time which has ALREADY PASSED stays on today, and the daylight
-saving reasoning, and neither could be asserted without an emulator.
The ViewModel keeps the sheet and the assignment; this type answers the questions. Same split as
`ProductAddFlow` and `BundlePlan`: the decision is pure, the awaiting is
not.

## At

```csharp
public static DateTimeOffset At(TimeSpan timeOfDay, DateTimeOffset now, TimeZoneInfo? zone = null)
```

<param name="timeOfDay">The clock time the operator chose.</param>
<param name="now">
What "now" is, from the ViewModel's `TimeProvider`. Passed in rather than read from
the clock here so the rule can be asserted at a fixed instant, and so the ViewModel stays the
only thing holding a clock.
</param>
<param name="zone">
The zone the promise is expressed in. Defaults to `TimeZoneInfo.Local`, which is what
`new DateTimeOffset(unspecified)` used — and using it implicitly is why this rule could
not be tested: the offset was resolved against the MACHINE's zone, so a ViewModel driven by a
TimeProvider pinned to some other zone would place the promise in one zone and print it in
another. Taking the zone from the same provider that supplied <paramref name="now"/> keeps the
two in step and lets the daylight-saving rule be asserted at all.
</param>

## DateTimeOffset

```csharp
return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
```

The offset for THAT wall-clock time, not for now. Across a daylight-saving change the two
differ by an hour, and an order promised for a time whose offset was taken from the wrong
side is an hour out for as long as it sits on the board.

## IsLate

```csharp
public static bool IsLate(DateTimeOffset? requestedAt, DateTimeOffset now) =>
```

A computed reading of the wall clock rather than a stored flag, so it cannot disagree with the
order. It is therefore only as fresh as the last time something announced it, which is why the
ViewModel re-raises it on every return to the tab.

## Describe

```csharp
public static string Describe(DateTimeOffset requestedAt, DateTimeOffset now, TimeZoneInfo? zone = null)
```

What the "когда" control says once a clock time has been chosen.
<param name="requestedAt">The promise now on the order. Not null: this describes a promise.</param>
<param name="now">What "now" is, from the ViewModel's clock.</param>
<param name="zone">
The zone to read the clock time back in. Defaults to `TimeZoneInfo.Local`, matching
`At`. It matters that the two agree: a promise placed with one zone's offset and
printed in another's is an hour out on the message the operator reads back to the customer.
</param>

**NON-NULLABLE, DELIBERATELY.** There is a case where nothing is promised — as soon as
possible — and it has its own sentence in `AsSoonAsPossibleMessage`. Folding that
case in here would mean returning `string?` and making every caller cope with a null that
its own control flow has already excluded; this type says what it means and the caller assigns
it directly.
Overdue is stated out loud, because it is not obvious from the figure. «Заказ к 14:20» at 15:05
is a different order from «Заказ к 14:20» at 13:00, and the sentence has to say which one this
is — otherwise the operator reads their own confirmation as a mistake.

## PhoneNumber

```csharp
public static class PhoneNumber
```

**NO EXTERNAL LIBRARY, AND THAT IS THE DECISION.** libphonenumber would restore a package
with its own metadata updates and build machinery for one field in one country. The whole of what
this app needs is: strip what people type around a number, put the country code in front, and
refuse anything that is not a number. When the café sells outside Russia, this class is where a
library arrives — not before, because a general parser brings national numbering plans, and a
till that accepts a number it cannot deliver to is worse than one that refuses it.

## MinDigits

```csharp
public const int MinDigits = RussianNumberDigits;
```

Lowest count this till accepts. Not an E.164 rule — E.164 states a maximum and no minimum —
but this is a Russian café, and a Russian number is `RussianNumberDigits` long, so
every shorter thing that reaches this method is a mistyped one. It errs towards refusing,
because a refused phone is typed again and a wrong phone goes onto a fiscal receipt.

## RussianNumberDigits

```csharp
private const int RussianNumberDigits = 11;
```

Digits in a Russian number written out in full: the country code 7 plus ten. Two things key off
it, for two different reasons that happen to be the same number — it is the shortest thing this
till will accept, and it is how "the country code is already written in the digits" is told
apart from "this is an 11-digit national number".

## Normalize

```csharp
public static string? Normalize(string? raw)
```

The storage form of a typed number: E.164, `+`, country code, digits — or `null`.
**RETURNS null RATHER THAN THROWING, ON PURPOSE.** An absent phone is a normal state, not a
fault: counter service never asks for one, and a customer may decline to give it. An exception
here would turn "the customer did not want to give a number" into an error message, and the
caller would have to catch something in order to do nothing.
What is refused is not "not enough digits" but "not a number": a letter or any other character
beyond the punctuation people put around a number, and more than `MaxDigits` digits.
A character that should not be there is a slip of the finger, and storing the slip is how a
contact list fills with numbers nobody can dial. Length alone is `IsValid`'s
question, and keeping the two apart is why a short-but-real foreign number can be looked at by
a person instead of being silently padded or silently dropped here.
E.164 is the STORAGE and transmission format; E.123 (spaced, as typed) is the display format.
Normalising now is cheap and normalising later is a migration over every row that already
holds free text, so the conversion happens at the edge, once, on the way in.
<param name="raw">Whatever was typed or pasted: digits, spaces, brackets, dashes, dots, a 7/8/9 in front.</param>
<returns>`+79XXXXXXXXX`, another `+`-number as typed, or `null`.</returns>

## Length

```csharp
_ when number[0] == RussianCountryCode && number.Length == RussianNumberDigits
```

The country code already typed with the whole number, "79161234567". Prefixing +7 to it
again would store "+779161234567": still 13 digits, so the length check would wave it
through, and a number nobody can dial would leave the till on a fiscal receipt. The
length is checked instead of trusting the first digit alone, because
"+7 701 123 45 67" is also eleven digits and would be mangled the other way round.

## IsValid

```csharp
public static bool IsValid(string? normalized)
```

Whether a normalised number is one this till is willing to store and to read out to a customer.
Expects the output of `Normalize` and answers a different question from it: parsing
asks "is this a number", this asks "is this a number we can use". A typed E.123 string returns
false rather than being accepted here — the pair is meant to be used in that order, and quietly
repairing a second time in two places is how two different answers end up in one column.

## Mask

```csharp
public static string Mask(string normalized)
```

The display form for the CUSTOMER's screen: `+7 ••• ••• •• 42`.
**WHY THIS SHAPE.** PCI DSS 3.4.1 asks for at least the first six and last four digits of a
PAN to be masked, and it is written about a card number, not a phone number: its figure is a
floor, not a shape, so hiding more than the floor asks is what compliance means here and not
what it costs. The shape kept is the country code, the middle in groups of three, and the last
two digits — 8 of 11 digits hidden on a Russian number, with enough left for the customer to
recognise their own number as they read it back to the cashier.
Free text that is not a number (only a '+', only punctuation) comes back as it went in: there is
nothing to hide in it and an operator seeing their own empty input echoed is more useful than a
row of bullets.

## Bounded

```csharp
private static string? Bounded(string assembled)
```

Assembles the result and enforces the E.164 ceiling. The ceiling is applied HERE rather than on
the input because every branch changes the count differently — the 8 becomes a 7, a leading 9
becomes a 79 — and a check made once, at the single exit, cannot be forgotten by a new branch.

## SaleResult

```csharp
public sealed record SaleResult(
```

What a sale attempt produced: how it ended, and the order when it ended by booking one.
<param name="Outcome">How the attempt ended.</param>
<param name="OrderNumber">The booked order's number, when booked.</param>
<param name="TotalPrice">What the order was booked for.</param>
<param name="ReceiptText">
The money sentence for the operator - which method, or that it is unpaid. It comes from
`PaymentBooking`, so the order and the message cannot disagree about what happened.
</param>

## Money

```csharp
public static string Money(decimal value) => Money(value, Currencies.Default);
```

The currency overload takes the currency explicitly; this one resolves
`Currencies.Default`, which the app sets from the operator's setting at
startup. "220,00 ₽" therefore becomes "220,00 ₿" for the Belarusian ruble and "220 ¥"
for the yen with no change at any of the call sites.

## CostPerUnit

```csharp
public static string CostPerUnit(decimal value, string? unit, Currency currency)
```

Formats a unit cost in the given currency as "12,50 ₿/г". Note the trailing zeros are
dropped for the fraction ("0.##") while `Money` pads to the currency's digit
count: a per-gram cost is a derived figure where "12,5 ₿/г" reads better than "12,50", and
a unit price never needs to reconcile against a printed total the way a total does.

## Plural

```csharp
public static string Plural(int count, string one, string few, string many)
```

ONE implementation for the whole app, and it was private to
`CatalogManagementViewModel` until a second screen needed it — which is exactly how
"1 товаров" gets written twice in two files and fixed in only one. It belongs here beside the
other Russian formatting helpers rather than in a ViewModel, because a word-choice rule is
not a view concern and this file is what the test project already covers.
The teen rule comes first and is not a special case of the units rule: 11–14 take «many» even
though their last digit is 1–4, so checking the units digit first gets 11 «позиция».
