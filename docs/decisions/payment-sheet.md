# Лист оплаты

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## XamlCompilation

```csharp
[XamlCompilation(XamlCompilationOptions.Compile)]
```

Serves BOTH directions of money: taking a payment and giving one back, told apart by
`PaymentSheetMode`. Sharing the sheet is deliberate - the keypad, the sizing, the
dimmed backdrop and the confirm flow were each measured once, and none of them care which way
the money moves. What does care is the wording, and every caption is rewritten for a refund
(see `ApplyMode`): the title, the amount caption, the confirm button, and whether
the cash/card row exists at all.
Reuses the measured fixes from `CatalogActionSheetPopup`: the ScrollView carries
`VerticalOptions="Start"` (without it the sheet stretched to its height cap), the width is
forced in code because the toolkit rewrites `HorizontalOptions.Fill` to `Center`, and
the dim comes from `PopupOptions.PageOverlayColor` in `Services.PaymentSheet`
because the toolkit insets popup content by 15dp per side.
The keypad is buttons rather than an `Entry` on purpose: the owner chose this because the
OS keyboard covers half the screen and is slow to bring up, and an `Entry` raises it on
every tap. Amounts are entered in whole rubles, so there is no decimal key.
**THE COMMON SALE IS ONE TAP, AND THE KEYPAD IS NOT IN THE WAY OF IT.** A coffee-shop sale
is the same number of taps whatever the tender, so the sheet opens on two full-width buttons
that each state the amount they charge and close on the tap. The keypad still exists — partial
payment is a real, supported outcome (`Order.BalanceKopecks`,
`PaymentState.PartiallyPaid`) — but it is behind «Другая сумма», a secondary control
clearly below the tender row.
**THE AMOUNT IS ON THE BUTTON.** Baymard's 2024 study captured a tester asking, of a «Next»
button, verbatim: «I'm not sure if I click the 'Next' button, will it charge?» — a button
naming only a method repeats that ambiguity, because the figure it would charge lives somewhere
else on the screen and has to be re-read. `PaymentSheetResult.Amount` already
carries a keyed amount, so the contract needed no new field: the same record means «exactly
AmountDue, cash» from a tender button and «60 ₽, whatever was typed» from the keypad.
**CARD NEVER OPENS THE KEYPAD.** There is nothing to choose for a card — it is always the
balance — so a keypad there would ask the operator to decide something that cannot be decided.
**IT IS ALSO THE «WHEN DOES THE MONEY MOVE» DECISION, ON THE CART PATH ONLY.**
«Оплата при выдаче» used to be a second button on the cart beside «Оплатить и создать»; it is now
`DeferredButton` here, under the confirm. It is a decision about when money moves rather than
a second kind of checkout, so it belongs where money moves — and on the other two paths that open
this sheet (the board's top-up, «Принять оплату» on the details page) the order already exists and
there is nothing to defer, which is why it is gated on
`PaymentSheetRequest.AllowDeferredPayment` and never on the mode alone. One tap does
not change that: it stays visible in both panel states.

## RefundCeilingRubles

```csharp
private long RefundCeilingRubles => (long)Math.Floor(request.AmountDue);
```

Floored rather than rounded because a refund above the collected amount is not a thing the
operator can mean: the cap is a promise about what the till still holds, and rounding 220.60
up to 221 would offer to give back 40 kopecks the drawer never received. The domain clamps
with the same floor, so the keypad and the ledger agree.

## BalanceRubles

```csharp
private long BalanceRubles =>
```

Floored, clamped at zero and held under `MaxEnteredRubles`. The floor is the
keypad's own contract — it has no decimal key, so a 220,60 balance can only be entered as
220 — and the service clamps the recorded payment down to the real balance afterwards, so the
quick-pay figure can never exceed what is owed. Clamping the cap in is what keeps a
pathological request from producing a prefill the keypad would refuse to replace.

## ZeroRefusalText

```csharp
private string ZeroRefusalText => request.IsRefund
```

No amount is named in the sentence, so no currency symbol is hardcoded in it — a hardcoded
«₽» here would ignore the operator's selected currency, which §5 of .opencode-rules.md
forbids for user-facing money.

## ApplyMode

```csharp
private void ApplyMode(PaymentSheetRequest request)
```

The direction rewrites every caption, not just the confirm button. That is the whole reason
the sheet knows which way the money is moving: «Оплата заказа» and «К оплате» shown over a
return are not a cosmetic mismatch, they instruct the operator to do the opposite of what
they came here to do — and they are the two strings on screen for the whole time they are
deciding how much to key in.
The method row is hidden on a refund rather than merely ignored, and the reason is that a
refund takes no method at all: the domain mirrors the payments it reverses so the drawer and
the terminal each balance against what they were actually credited. A visible-but-ignored
choice would be worse than no choice — the operator would reasonably believe the return was
booked the way they picked it.
A refund gets NO quick pay and NO prefill. It keeps the keypad it always had, starting at 0,
because prefilling it with the ceiling would make «Вернуть оплату» a one-tap return of
everything the till is being asked about — the single most dangerous button this sheet could
grow. The operator chooses how much to give back.

## OnTenderClicked

```csharp
private void OnTenderClicked(object? sender, EventArgs e)
```

The amount is `BalanceRubles` and not the entered one, because there is no entered
one on this path — nothing has been typed. The figure is floored, which is the keypad's whole
rubles contract, and the service clamps a payment down to the real balance afterwards, so this
can charge less than the balance but never more.
`PaymentSheetResult` is returned in exactly the shape the keyed path returns it in.
That is the whole point of the design: the callers cannot tell the two apart, and none of them
needed changing to get one-tap payment.

## OnOtherAmountClicked

```csharp
private void OnOtherAmountClicked(object? sender, EventArgs e)
```

The prefill is there so the common case — «pay the balance, but let me type it» — needs no
typing at all, and `replaceEnteredOnNextDigit` is set with it so the first digit
REPLACES the balance instead of appending to it. Appending would be the worse bug of the two
and a quiet one: 160 prefilled, then «5» and «0» would read 1 6 0 5 0 and be refused by the
refund-style cap logic only on the collect path's behalf, so the operator would be told
nothing and would conclude the keypad is broken.
Collect only. A refund never reaches this handler — there is no «Другая сумма» — so the
refund keypad still starts at 0.

## RefreshPanels

```csharp
private void RefreshPanels()
```

Three panels, three independent flags, and every combination is reachable:
quick pay (collect, balance &gt; 0, keypad closed), the keypad (collect after «Другая сумма»,
or any refund), and the nothing-due label. Nothing is hidden behind a rule without its
button, which is the layout fault the deferred separator comment warns about.

## OnConfirmClicked

```csharp
private void OnConfirmClicked(object? sender, EventArgs e)
```

The entered amount is passed as typed; the service clamps it to the balance. The change
the operator hands back is entered − balance, which is what ChangeLabel showed.
Zero is REFUSED WITH A REASON rather than a disabled button. «Оплатить ноль» is not a thing:
a zero-value row would still be written to `OrderPayment`, would leave
`PaidKopecks` unchanged, and would land in the shift reconciliation as a row that
accounts for nothing. A greyed button says «not now» and leaves the operator to guess;
this says why, in a sentence, next to the control they pressed.

## OnDeferredClicked

```csharp
private void OnDeferredClicked(object? sender, EventArgs e) =>
```

The amount and method are written as fillers rather than as what the operator entered, and both
are meaningless on this path — `PaymentSheetResult.IsDeferred` is the whole message,
and the caller branches on it before it reads either. `enteredRubles` is passed as 0 on
purpose: whatever the operator keyed in before changing their mind is not a payment, and
handing the caller a figure it might mistakenly book is a worse failure than handing it a zero
it cannot.
Dismissal is the same outcome as tapping outside the sheet, so this is a CloseAsync like any
other — a caller that gets a non-null result has a decision either way and must read the flag.
**And one tap does not change this: the button is live on the quick-pay panel as well as on
the keypad panel**, so an operator who pays on collection has not lost the exit to the
sheet's new default. Deferring is not a fallback for being unable to charge the balance; it
is the other answer to the same question, and it is gated on the caller, never on the panel.

## Refresh

```csharp
private void Refresh()
```

The confirm button is deliberately NOT disabled at 0 any more. See `OnConfirmClicked`:
a refusal the operator can read beats an inert control they have to interpret, and the amount
on the quick-pay buttons has already made the common case a single tap that never touches this
method — so there is no short-circuit being removed here, only an unexplained grey button.

## SizeToWindow

```csharp
private void SizeToWindow()
```

The height is only a cap, never a target: `MaximumHeightRequest` stops the ScrollView
growing, and the ScrollView's own `VerticalOptions="Start"` is what makes it stop at its
content.
The bottom inset is the real system navigation-bar height read from the platform, not a
hand-picked number: the sheet is pinned to the window's bottom edge, and on an edge-to-edge
Android window the navigation bar covers that edge, so the confirm button would sit under it
without this. Read from the `navigation_bar_height` resource — the same one MAUI's own
platform code reads — so it tracks the actual bar. Zero on platforms with no bottom bar.

## replaceEnteredOnNextDigit

```csharp
private bool replaceEnteredOnNextDigit;
```

True while the prefilled balance is behaving as if it were SELECTED text: the next digit
replaces it rather than appending to it. The keypad has no cursor and no selection, so this
flag is what «the text is selected» means here, and it is cleared by the first key — which
includes «Сброс» and «Стереть», because those are deliberate acts rather than typing.

## GetBottomInset

```csharp
private static double GetBottomInset()
```

The height of the system navigation bar in device-independent pixels, or 0 when there is
none. Android-only: the other platforms either have no bottom bar or inset their content
themselves.
