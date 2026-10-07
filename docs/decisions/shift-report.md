# Отчёт по смене

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## SyncMovements

```csharp
private void SyncMovements(IReadOnlyList<CashMovement> movements)
```

NOT `ObservableCollectionSync.SyncWith`, and at the time of writing the reason was a crash
rather than a preference. `SyncWith` replaced a row whose key was unchanged but whose
instance differed, which is every reload here because these rows are classes; MAUI's
`BindableLayoutController.ReplaceChild` indexes into its own list of realised children and
throws `ArgumentOutOfRangeException` when a reload happens before that list exists. The
exception was raised inside this reload, caught by the page's own handler and shown to the
operator as «Не удалось загрузить смену» — with the drawer figures above it already updated,
which is the worst shape a partial failure can take.
`SyncWith` no longer raises Replace at all — it re-creates a changed row AT ITS INDEX or
refreshes it through a delegate — so it would be safe here too. This method stays anyway,
because for THIS list it is the clearer statement of what the list can do: a movement is
written once and never edited, a correction is a new row rather than a change to an old one,
and so the only two operations the list ever needs are Add and Remove. Add/Remove/Reset are
the actions BindableLayout applies through layout.Insert / layout.RemoveAt / CreateChildren(),
so nothing here depends on the collection and its realised children agreeing by index.
The reason it is not `SyncWith` was never that a movement cannot change in place. It is
that `OrderRowViewModel` — the row `ShiftHistory` is built from — has
`Model { get; }` with computed properties, so there is nothing on it to write: a changed
row can only be replaced by a NEW instance. `SyncWith` now does exactly that at the same
index, which is why the history list needed no second sync method to become safe.
O(n²) over a handful of rows, which is not a cost worth optimising away.

## SyncDiscountedLines

```csharp
private void SyncDiscountedLines(List<DiscountedLine> lines)
```

Clear + Add, and NOT `ObservableCollectionSync.SyncWith`, for one reason:
`DiscountedLine` carries no identifier of its own, so any key would have to be
assembled out of the values themselves — and two overridden lines that agree on every value
would then collapse into a single row. In an audit list a silently missing row is a worse
defect than a list that is re-created on load, and this list is re-read only when the page
appears or a reload is asked for.

## CancelOrderAsync

```csharp
private async Task CancelOrderAsync(OrderRowViewModel? row)
```

Deliberately the same three dialogs in the same order as the board's cancel flow
(confirm, disposition, reason) and with the same wording, because they are the same
operation. The difference is entirely in what it costs: here the order is finished and paid,
so the confirmation states the amount that goes back and the outcome message reports it.
The page reloads rather than patching the row. A cancellation writes refund rows to the
ledger, changes PaidKopecks and can move stock; the row on screen shows status and payment
state, and a half-updated card is worse than a re-read one. It is also the only way the
money block above the list can pick up the refund it just caused.

## CloseShiftAsync

```csharp
private async Task CloseShiftAsync()
```

The count is not a step in this flow, it IS the flow: `CloseShiftAsync` takes the
counted amount and refuses a shift closed without one, so a shift that closes has a
reconciliation attached to it. There is no "skip" branch and none is wanted — a count that
can be declined is a count that does not exist.
ORDER OF OPERATIONS, and the order is the design. Confirm, then count, then reason, then
backup, then close:
<list type="bullet">
<item>The count is asked for BEFORE the backup, because the backup is wasted work if the
operator then abandons the dialog — and the dialogs are the only part of this the operator
can genuinely back out of.</item>
<item>The count is asked for before the close, not after, so a mismatch is discovered while
the drawer is still on the table and the operator can count it again. Discovering a
shortage after the fact, with the money in a bank bag, is the worst order available.</item>
<item>The backup stays immediately before the write, which is the only moment it can protect
anything: everything after it is either a dialog or the reload.</item>
</list>
The PROMPT IS PRE-FILLED with the live drawer figure. In the overwhelmingly common case the
count matches, and then the whole reconciliation costs one tap on a value the app already
knows. Hand-transcribing a figure the screen is already showing is a transcription task, and
transcription fatigue has a known failure mode: after a dozen shifts of typing zeroes into
fields that are all the same shape, the zeros keep coming. Removing the typing is the
cheapest accuracy win available anywhere in this feature.
The pre-fill deliberately shows the figure ON SCREEN, not a freshly re-read one. A pre-fill
that disagreed with the «Итого наличными в кассе» line a few centimetres above the button
would be a number the operator cannot reason about; if the screen is stale they press
«Обновить». The failure mode of a stale screen is benign anyway — the domain compares
against the truth and answers with both figures, and the operator corrects the count.

## CollectCashCountAsync

```csharp
private async Task<CashCountEntry?> CollectCashCountAsync()
```

Two failure modes are handled differently on purpose. An ABORT (whitespace, an unparseable
number, a missing reason) returns quietly and writes nothing: the shift is untouched and the
operator decides when to try again. A DOMAIN REFUSAL is left to propagate to the caller's
catch, because by then the count is on its way into the shift and the operator has to be told
why it is not there.
The expectation is carried back in the entry next to the count. The caller needs it to word
the outcome message, and reading it off the ViewModel after the close would mean reading
`CashInDrawer` — a value `LoadAsync` is about to overwrite with the NEW shift's
figures. Carrying the pair means the message cannot be assembled from a number that moved.

## PromptDiscrepancyReasonAsync

```csharp
private async Task<string?> PromptDiscrepancyReasonAsync(decimal expected, long countedKopecks)
```

Both figures are repeated in the prompt. The operator is being asked to explain a specific
number, and a prompt that says only "причина расхождения" invites a reason for the wrong
discrepancy — the one they remember from this morning, not the one on the screen.

## DescribeCloseFailure

```csharp
private static string DescribeCloseFailure(Exception exception) => exception switch
```

An `AppException` from this flow is already a Russian sentence written for the
operator — «Нельзя закрыть смену: осталось незакрытых заказов — 3, из них не оплачено 2 на
120,00 ₽» — so it is shown on its own. Prefixing it with the generic «Не удалось закрыть
смену: » produced "Не удалось закрыть смену: Нельзя закрыть смену: …", which reads as a
stutter and buries the part that matters. Every other failure still goes through
`UserMessages.Describe`, whose last arm is what keeps an unknown error from
reaching the operator as a bare type name.

