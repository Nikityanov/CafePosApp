# Карточка заказа

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## PaymentLine

```csharp
public sealed class PaymentLine : ObservableObject
```

`IsRefund` is a separate field rather than something the wording implies,
because the two rows are the same shape in every other respect and a preformatted
single-string line made a refund line indistinguishable from a collection at the
template level. It is what `AmountColor` reads, and the template cannot
parse the text to find out the direction.
`AmountText` is the signed, formatted figure on its own and `Text`
is the composed sentence, split so the template can put the amount in its own column. The minus
sign is part of `AmountText`: the sign is the fastest thing to read on a row
list, and it must not depend on the surrounding prose to be noticed.
This was a positional record and became an ObservableObject, because `AmountText`
is now a re-derivable binding rather than a value frozen at construction: the operator can
change the currency while this line is on screen, and a string built once cannot follow. See
`RefreshMoneyText`.

## AmountColor

```csharp
public Color AmountColor => IsRefund
```

This tint has been through two wrong answers, both worth recording. It was plain
`{StaticResource Danger}` inside a `DataTrigger` Setter, which a trigger resolves
once at parse time — a trigger's Setter takes a VALUE, not a binding expression — so the
light token stayed in dark theme. It was then "fixed" by putting an
`AppThemeBinding` in that same Setter, which is no better: an AppThemeBinding needs an
`IProvideValueTarget` to register its theme-change callback against, and a trigger
setter is not one, so it too collapses to a single parse-time value. Only a bound property
resolves against the live theme. See MenuViewModel.MessageColor, which is the same story.
Numbers: this label is 12pt bold, which is UNDER the 14pt-bold large-text threshold, so it
owes 4.5:1 as body text. The page has no CardBorder behind these rows, so the backdrop is
the ContentPage fill — Danger `#D32F2F` on `BackgroundDark #121212` is 3.76:1 and
fails; DangerDark `#FF9E8E` on the same surface is 9.40:1. Light theme is unchanged at
Danger on `#FAFAFA`, 4.77:1, which passes.
The untinted branch has to NAME a colour rather than defer to the implicit
`Style TargetType="Label"`, because a local binding suppresses that style's own setter.
`Resolve("Black", "White")` reproduces exactly what it declares
(`AppThemeBinding Light=Black, Dark=White`, Resources/Styles/Styles.xaml). If that
implicit style changes, this has to change with it.
The tint is the THIRD signal and never the only one: the row text says «возврат» and the
amount carries a minus sign, and `SemanticProperties` hands a screen reader the word,
not the colour.

## ListPrice

```csharp
public decimal ListPrice { get; init; }
```

Shown struck through beside a changed price so the cashier sees the override while making it,
exactly as on the cart. The allowed price is written once, when the line is created, and
`UpdateOrderAsync` deliberately does not recompute it on an edit — recomputing it would
write the new charged price straight back into the allowed price and leave the shift report
nothing to compare.

## MergeKey

```csharp
public string MergeKey => OrderLineKey.For(
```

`OrderLineKey` and not an inline comparison, which is what this row's lookup used
to do — and the inline version left the VARIANT out of the key entirely, so a large and a small
of the same dish merged into one line. The composition is in the key too, so two different
builds of one bundle stay two lines.

## ToOrderItem

```csharp
public OrderItem ToOrderItem() => new()
```

The composition is carried through, and that is load-bearing rather than cosmetic.
`UpdateOrderAsync` matches incoming lines to the stored ones by `OrderLineKey`
— which is built from the composition — and then rewrites a matched line's snapshot wholesale.
A row that dropped its slots would therefore fail to match its own line, be inserted as a NEW
line with no composition, and leave the original bundle composition behind on a line that no
longer exists: a save of an untouched order would silently un-compose its bundles.

## FulfilmentSummary

```csharp
public string FulfilmentSummary => order is null
```

Collapsed, it states the order as it stands («В зале · готово к 18:00»). Expanded, it says
only «Параметры заказа» and nothing more — the block directly beneath it reads «Выдача: В зале»
and «Готово …», and repeating them in the row would print each fact twice inside two rows.
Same rule and same one-property construction as `MenuViewModel.FulfilmentSummary`,
because the two order-entry screens are deliberately one design: both read their state from
`IsFulfilmentExpanded` here, never from a second string kept in step by hand.

## CustomerPhoneText

```csharp
public string CustomerPhoneText => order?.CustomerPhone ?? string.Empty;
```

The one unmasked number in the app. This page is opened deliberately, for one order at a time,
and it is the screen a cashier has to dial from — a masked number here would make the stored
value unverifiable and the customer unreachable, which is the opposite of what 152-ФЗ
ст. 6(1)(5) asks for. Everywhere else — the cart and the orders board — the number is masked,
because those two are readable by whoever is standing in the room.
Empty for a counter order, and not merely hidden: `CheckoutService` drops the value
at the service boundary, so there is nothing stored to show and this is an honest blank rather
than a masked nothing.

## ExpectsNoPhone

```csharp
public bool ExpectsNoPhone => order is not null && !ExpectsPhone;
```

Said in words rather than left as a blank. «Телефон не спрашивали» and «телефон забыли» are
different facts about the same empty row, and for an order eaten on the premises only the first
one is correct — 152-ФЗ ст. 6(1)(5) permits the field only where the number is needed to perform
the contract, so its absence here is the rule working, not a gap in the data.

## PromiseText

```csharp
public string PromiseText => order is null
```

A named time reads as «к 18:00» and the lead-time one as «обещано к 18:00». The customer saw a
RANGE on the cart because that figure is an estimate the till chooses; here the exact time is
what the order was promised and is being judged against, and a staff screen showing an estimate
where a fact exists would be the wrong way round.

## TotalText

```csharp
public string TotalText => TextFormat.Money(Total);
```

The total is a sum over `Items`, so it changes whenever a line is added, removed
or re-quantitied — none of which touch this ViewModel's own properties. Every mutation site
therefore calls the private NotifyTotal(); the per-item quantities raise only their
own LineTotalText, because the footer is this property's job.

## CanCollectPayment

```csharp
public bool CanCollectPayment =>
```

Unchanged by the refund feature, and deliberately so. The status clause is what makes it
safe: refunds only exist on `OrderStatus.Completed` orders, and a refunded order
is still Completed, so a refunded order can never be re-charged through this path even though
its `PaymentState` has fallen back to Unpaid or PartiallyPaid. Verified against the
partially refunded case specifically — money held for it is money still in the till, and
taking a second payment against it would credit the drawer twice for one sale.

## CanRefundPayment

```csharp
public bool CanRefundPayment => order is not null && order.Status == OrderStatus.Completed && order.PaidKopecks > 0;
```

The two conditions together, and neither is implied by the other. `Completed` because
the domain restricts refunds to finished sales — the goods have left the bar, so giving the
money back is the operator's decision, not a correction to a running total.
`PaidKopecks &gt; 0` because it is the NET scalar: after a full refund it reads zero, so
this is the check that stops the button appearing on an order that has nothing left to give
back. Without it, an operator could open the sheet on a fully refunded order, key in an
amount and be refused by the domain's «По заказу нечего возвращать» — the exact
"control that can only fail" pattern the cancel button used to have.

## CanAddContactDetails

```csharp
public bool CanAddContactDetails =>
```

Paid is the condition, and it is the owner's whole reason for the button — the customer thinks of a
number AFTER the money has changed hands, so an order that is still unpaid is handled by the cart
sheet it already came from, and offering the same facts twice in two places would let them disagree.
A voided order is excluded because its money went back and its total is gone: a customer who
"remembers a number" for it is describing a different sale, and Core refuses it too. The two checks
exist in both places deliberately — this one so the button is not offered for nothing, that one so
a caller that skips the ViewModel still cannot write.

## CanCancel

```csharp
public bool CanCancel => order is not null && order.Status != OrderStatus.Cancelled;
```

Anything not already cancelled, for the same reason the board's `CanCancel` is: a paid
order is voidable and cancelling takes the money with it. An already-cancelled order is
excluded because the domain makes cancellation single-shot, and offering the control anyway
would offer one that can only fail.

## PaymentSummary

```csharp
public string PaymentSummary => order is null
```

Branches on `Status` FIRST, then on the money — the ordering is the fix. This used to
switch on `PaymentState` alone, which after a full refund renders
«Не оплачен · к оплате 220.00 ₽» on a finished sale whose money has already gone back to the
customer: correct arithmetic, completely wrong story, and it asks for money that cannot be
taken because the order is closed. "Voided of its payment" is not "unpaid", and the only way
to say that is to look at the status before the figures.
A cancelled order reads the same way — the money went back as part of the cancellation, so
there is nothing owed and nothing to collect. The stock disposition is left out of this line
because `StatusText` above already carries the composed cancellation reason, including it.
Every branch names its amounts. Never colour alone: this line is the only place the state is
stated in words, and a screen reader reads the sentence.

## DescribeClosedOrder

```csharp
private string DescribeClosedOrder()
```

A partially refunded order deliberately does NOT fall into the "unpaid" wording even though
its `PaymentState` is now `PaymentState.PartiallyPaid` — because it can still
hold money (`PaidKopecks &gt; 0`) or hold none at all (fully refunded), and those are
different sentences from anything the open-order branch below says, which promises to collect.

## PaymentColor

```csharp
public Color PaymentColor => order switch
```

Was `Microsoft.Maui.Graphics.Colors.*` literals, which cannot follow the theme — see
`ThemeColors`. Concretely: `Colors.Gray #808080` is 4.22:1 on a
`SurfaceDark #1E1E1E` card and 3.49:1 on a `SurfaceVariantDark #2D2D2D` one, and
this is a 13pt bold line carrying the whole payment story of a closed order. The palette's
`Gray600`/`Gray400` — already this app's secondary-text pairing — gives 4.61:1
in light and 8.87:1 in dark, with the same neutral reading. Paid, partial and unpaid move
to `Success`/`Warning`/`Danger` and their dark counterparts.
The branch structure is untouched. It was already right, and it is why colour here is never
the only cue: `PaymentSummary` states the same thing in words, including the
figures, and it is what a screen reader reads.

## ShowOrder

```csharp
public void ShowOrder(Guid id)
```

This used to be MAUI Shell's `ApplyQueryAttributes`, which meant implementing an interface
from the UI framework to receive one Guid. The interface is a routing contract, not behaviour:
the whole method was "read OrderId out of the dictionary, set the field, load". The page now
does the dictionary part and calls this, so the ViewModel no longer names Shell at all —
which is what lets it live in a project that references Microsoft.Maui.Graphics and nothing
else.
The fire-and-forget load is unchanged from the original: navigation does not wait for the
screen's own data, and `IsBusy` is what the page shows while it happens.

## EditItemPriceAsync

```csharp
private async Task EditItemPriceAsync(OrderEditItemViewModel? item)
```

Same rule as on the cart and for the same reasons: no reason is asked for, because a reason
collected at the till becomes the first value anyone ever clicks, and nothing is refused,
because a POS that argues with a cashier about a price teaches people to work around it. The
allowed price is written once when the line is created and `UpdateOrderAsync` deliberately
does not recompute it, so an override made here cannot be laundered back into "this is what it
should have cost".

## EditItemCompositionAsync

```csharp
private async Task EditItemCompositionAsync(OrderEditItemViewModel? item)
```

One mechanic, two places. A second editor here would be how a shop grows the "bundles stopped
showing in reports" bug: two definitions of what a bundle is, and only one of them feeding the
sale.
The line keeps the bundle's OWN price — changing the composition does not reprice it. The sum
of the slots is the à la carte reference the discount is measured against, not the price. The
change is not written until «Сохранить» like every other edit on this page.

## RefundPaymentAsync

```csharp
private async Task RefundPaymentAsync()
```

A `ConflictException` with "уже оплачен" is not an error: the order may have been
settled on the board since this page loaded. It is treated as a reload at warning level.
Amount first, then reason, and that order is not arbitrary. The amount comes from the keypad
so the operator sees the ceiling ("Вернуть можно: …") while deciding, and the reason is typed
afterwards — the domain stores it on the refund rows and truncates it at 300 characters, so a
free-text prompt before the amount would be typing into a form for a transaction whose size
was not yet chosen.
No method is passed, and that is not an omission. The domain mirrors the payments it
reverses, oldest first, because the drawer and the terminal are two real tills: booking the
return under a method the money never arrived in would leave one of them wrong at the count
with nothing in the app to say so.
The reason is REQUIRED here, unlike the cancellation reason next door. It is the only record
of why money left the till on a sale that genuinely happened, so it lands on the refund rows
and is what an auditor reads first. An empty prompt result aborts: an unexplained refund is
the one entry nobody can reconstruct afterwards.

## AddContactDetailsAsync

```csharp
private async Task AddContactDetailsAsync()
```

The sheet is asked FIRST and the write happens SECOND, and the order matters: the fulfilment-mode
question can only be asked when the operator has said they want to record something. Opening a
sheet that then refuses to save would be a worse board than no button.
**THE PHONE CONTOUR IS NOT DECIDED HERE.** Whether a number may be stored is Core's answer, from
`ContactPhoneRule` and `AddContactDetailsAsync`. This method passes
`PromoteToTakeaway` through as a RECORD of what the operator ticked and lets Core refuse the
write when that tick is missing — which is why the catch below exists and why its message comes
from the exception rather than from a string built here. A ViewModel that second-guessed the phone
would be a second copy of 152-ФЗ ст. 6(1)(5), in a file with no test over it.
A dismissed sheet returns `null` and nothing is written: a dismissal is not an answer, and
treating it as one would let a stray back-gesture clear a stored number's edit.

## CancelOrderAsync

```csharp
private async Task CancelOrderAsync()
```

Kept here as well as on the board because the details page is where an operator already is
when they have picked one specific order to undo — and until this existed the page had no
cancel control at all, so a mistake on a paid order had to be fixed by finding the right row
on a different screen.
Reloads instead of navigating back: the order stays on screen and now reads
«Отменён, возвращено …» with the refund rows in the ledger beneath it. Going back would hide
the only confirmation the operator gets that the money actually moved.

## BuildPaymentLine

```csharp
private static PaymentLine BuildPaymentLine(OrderPayment payment)
```

A refund is the same row shape as a payment: same table, same method vocabulary, positive
amount, direction carried by `IsRefund`. That is right for the invariant and wrong for a
human reading a list, so before this a refund row rendered exactly like a collection row, one
going in and one coming out with nothing on screen saying which was which.
Three signals, and all three are needed because this app never signals state by colour alone:
the word "возврат" in the row text, a leading minus sign on the amount, and the danger colour
the template applies from `PaymentLine.IsRefund`. Drop the word and a screen
reader user cannot tell the rows apart; drop the sign and an operator scanning a column of
figures reads the direction the wrong way; drop the colour and the two rows look like one.
The method is still named on a refund even though the operator never chose it, because it is
the method the money left by, which is exactly what a manager needs when reconciling a drawer
against a terminal report. It is worded as "возврат наличными" rather than as a collection so
the sentence matches the direction instead of fighting it.

## NotifyTotal

```csharp
private void NotifyTotal()
```

`Total` is the decimal sum and `TotalText` is what the footer actually binds — the
formatted amount in the operator's currency. Both are raised together: the decimal one for
anything still reading the raw figure, the text one because a bound Label is never told about
a dependency's change on its own.
