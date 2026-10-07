# Касса, смена и деньги

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## CashMovementKind

```csharp
public enum CashMovementKind
```

Two kinds and no third. A `Float` is change going IN — the opening float or a top-up during
the shift — and a `Payout` is cash taken OUT for collection. Both exist because the drawer
is not only sales: a till that has no change cannot break a 1000 ₽ note, and that is not a
bookkeeping detail, it is the till refusing to do its job.
There is deliberately no `Correction` member. A wrong amount is not a third kind of thing
that happened to the drawer, it is the same thing recorded wrongly, and the correction is the
same movement pointing back at the original through `CashMovement.ReversesMovementId`.
A `Correction` member would let a row exist with no movement to correct, which is a hole in
the ledger rather than an entry in it.

## CashMovement

```csharp
public class CashMovement
```

APPEND-ONLY. No row here is ever updated or deleted, which is the same rule
`OrderService.CloseShiftAsync` applies to the end-of-shift count and for the same reason: a
figure that can be silently overwritten stops being evidence. A mistake is answered with another
row, not with an edit.
**THE AMOUNT IS ALWAYS POSITIVE.** Direction is `Kind`, flipped by
`ReversesMovementId` — never a negative `AmountKopecks`. A signed column
would put the same fact in two places: the number says how much and its sign says which way,
and any code that forgets the second reads a payout as money arriving. Keeping the amount
unsigned means `SignedKopecks` is the ONLY place direction is decided, so there is
exactly one definition to keep in step.
A `Payout` is refused when it exceeds what the drawer holds at that moment, so this
table alone never drives the balance negative. The balance can STILL go negative another way: a
cash refund taken after a payout leaves money the drawer no longer has. That is allowed on
purpose — the refund's own rules are the carefully tested part of this app, and physically the
money comes from a reserve outside the drawer — and the negative balance is shown rather than
hidden. See `CashLedger`.

## Reason

```csharp
public string? Reason { get; set; }
```

Why the money moved. Mandatory on a `CashMovementKind.Payout` — money leaving the
till needs an account of itself — and optional on a `CashMovementKind.Float`,
because "put the change in" needs no elaboration. Never truncated: an over-long reason is
refused, the same rule `CloseShiftAsync` applies to a discrepancy reason, for the same
reason.

## ReversesMovementId

```csharp
public Guid? ReversesMovementId { get; set; }
```

Self-referencing rather than a `CashMovementKind.Correction` member, so the link is a
fact in the data and not a convention a reader has to know. The original row stays exactly as
it was written, which is the entire point: the drawer has two rows saying what happened, one
saying what was wrong and one undoing it.
A movement can be cancelled at most once, and only while its shift is still open. Both rules
are enforced by `CashLedgerService` rather than by the database, because SQLite has
nothing to say about them and a constraint that fires on a hand-edited database is worse than
a check that reports what is actually there.

## SignedKopecks

```csharp
public long SignedKopecks => Kind == CashMovementKind.Float ? AmountKopecks : -AmountKopecks;
```

The single definition of direction. Summing this column over a shift's movements, and adding
it to the cash payments, is the whole of "what is in the drawer".
`ReversesMovementId` does NOT enter this arithmetic, and that is worth stating
because the obvious first implementation negates on it and is wrong twice over. A correcting
row carries the OPPOSITE kind from the movement it undoes — cancelling a collection puts money
back, so it is a `CashMovementKind.Float` — which means the kind already says
which way it went. Negating again would turn a 500 ₿ collection that was corrected back into a
500 ₿ deposit, and the drawer would drift by exactly the mistakes somebody bothered to correct.
The link is a fact about the ledger's history; the sign is a fact about the cash.

## CountedCashKopecks

```csharp
public long? CountedCashKopecks { get; set; }
```

── Cash reconciliation ────────────────────────────────────────────────────────────────
Four columns, none of them derived and none of them re-writable once written. The difference
between the count and the expectation is NOT one of them: it is Counted − Expected, and a
stored copy would be a third copy of the same arithmetic.

## CountedCashKopecks

```csharp
public long? CountedCashKopecks { get; set; }
```

What was physically counted in the drawer at the close, in kopecks. NULL = never counted,
which is a fact of its own and is what every shift that predates the feature holds; that is
why the column is nullable rather than defaulting to 0. A count of `0` is a REAL count,
not an absent one — an empty drawer is missing money, and reading it as "not counted" hides
exactly the case worth catching.

## ExpectedCashKopecks

```csharp
public long? ExpectedCashKopecks { get; set; }
```

A FROZEN snapshot of the ledger's drawer figure at the moment of the close
(`PaymentsCash − RefundsCash`), in kopecks. Frozen on purpose and written once: this is
what the count was compared against, and it must keep saying that even after later refunds
move the live figure — otherwise "the drawer was 120 short" silently becomes a different
statement every time somebody takes money back out. Read the live figure from
`ShiftStats.ExpectedCashNow`; `ShiftStats.IsReconciliationStale` is what reports that
the two have parted company. NULL = never counted (see `CountedCashKopecks`).

## ReconciledAt

```csharp
public DateTimeOffset? ReconciledAt { get; set; }
```

When the reconciliation was RECORDED, not when the cash was physically counted. It equals
`EndTime` to the second, because the service records the close moment and the
count is typed into the same flow — a small lie about the three minutes of counting that
happened before it. It is kept anyway: it is the audit "when", and it is what makes a future
recount cheap to add. Nothing derives anything from it.

## CashMovements

```csharp
public List<CashMovement> CashMovements { get; set; } = new();
```

This is the FOURTH thing that is not derived from orders, and it is the one that decides
whether the drawer figure is right. Before it existed, "what is in the drawer" was exactly
"cash taken minus cash given back", which is true only for a till that was opened empty and
never had anything taken out of it. A café that puts 500 ₿ of change in at 08:00 and bags
3000 ₿ at 15:00 has a drawer that is neither.
Read through the DbSet and folded by `CashLedger`, never through this collection: a
shift with a long day behind it carries hundreds of movements, and materialising them onto an
order-aggregating entity is how a shift load turns into a page of cartesian work.

## CashLedger

```csharp
internal static class CashLedger
```

THE ONE DEFINITION. `ShiftPayments` used to carry it as
`Totals.CashInDrawerKopecks` — "cash taken, less cash given back" — and that is the whole
reason this class exists. It is true only for a till opened empty and never emptied: a café that
puts 500 ₿ of change in at 08:00 and bags 3000 ₿ at 15:00 has a drawer that is neither, and
against it the end-of-shift count would report a shortage of exactly the change.
So the drawer figure is defined HERE and only here, as four added facts rather than two:
the float that went in, the cash that arrived through orders, the cash that went back out
through refunds, and the cash that was carried away.
**THE NON-NEGATIVITY THAT IS NOT A GUARANTEE ANY MORE, STATED HONESTLY.**
`ShiftPayments` argued the drawer can never go below zero, and the argument was
sound — it rested on refunds never exceeding collections, which
`PaymentRecorder.AllocateMirroredSlices` guarantees per order and which therefore sums to
the same inequality over a shift. Movements break the argument in two different ways, and both
were considered:
<list type="number">
<item>A payout larger than the drawer. Refused outright, in `CashLedgerService`, against the
balance as it stands at that moment. This is the case a check can actually prevent.</item>
<item>A cash refund taken AFTER that payout. Not preventable and not prevented: the refund's own
rules are the carefully tested part of this codebase, and physically the money comes from a
reserve outside the drawer, so a negative balance is a fact about the world rather than a bug.
0 ₿ float, 1000 ₿ of cash sales, 1000 ₿ carried away, then a 1000 ₿ cash refund, leaves −1000 ₿,
and every one of those four steps is legitimate.</item>
</list>
So the rule the code can hold to is the precise one: a movement that takes money out may not
exceed the drawer as recorded up to that moment. The balance afterwards may still be negative,
and it is shown rather than hidden, because a hidden negative is indistinguishable from a bug in
the arithmetic.

## movements

```csharp
var movements = await db.CashMovements.AsNoTracking()
```

Folded through the same rule CashMovement.SignedKopecks applies — see that property for why
a correcting row's KIND, not its ReversesMovementId, decides the direction. The rows are
materialised rather than summed in SQL: an expression like "Kind == Float ? Amount : -Amount"
transliterates happily, works today, and is a second copy of a rule that also lives on the
entity. A shift holds as many movements as a busy day has floats and collections: tens of rows.

## struct

```csharp
internal readonly record struct Totals(
```

The payments aggregates are carried here rather than read separately by the report so that
"one read, one definition" holds. `ShiftPayments.Totals` is reachable through
`Payments`, and it deliberately exposes no drawer figure of its own — see that
type for why it no longer has one.

## RecordFloatAsync

```csharp
Task RecordFloatAsync(long amountKopecks, string? reason = null, CancellationToken cancellationToken = default);
```

Puts change into the drawer of the active shift.
<param name="amountKopecks">Magnitude, kopecks. May be 0 and may never be negative.</param>
<param name="reason">Optional — "put the change in" needs no elaboration.</param>

## RecordPayoutAsync

```csharp
Task RecordPayoutAsync(long amountKopecks, string reason, CancellationToken cancellationToken = default);
```

Takes cash out of the drawer of the active shift for collection.
<param name="amountKopecks">Magnitude, kopecks. May never be 0 or negative.</param>
<param name="reason">Mandatory. Money leaving the till has to account for itself.</param>

## CashLedgerService

```csharp
public sealed class CashLedgerService(
```

Separate from `IOrderService` because none of this touches an order. It is also the
only place in the app where money moves with no customer behind it, which is exactly why the
rules are here and not spread across view models.
**THE ACTIVE SHIFT, ALWAYS.** No method takes a shift id. A movement recorded against the wrong
shift is the same class of mistake as reconciling one shift while closing another — the money
left the drawer of the shift that was open, and booking it anywhere else is a lie that a
reconciliation will later report as a shortage.
**WHY A PAYOUT IS CHECKED AGAINST THE DRAWER AND A REFUND IS NOT.** A payout is refused when it
exceeds the balance as recorded at that moment, because it is the one movement whose absence
would silently invent money: the drawer figure would rise by an outgoing movement nobody
recorded, and the count at the close would balance perfectly against a lie. A refund takes a
different path and is not checked — see `CashLedger` for why the balance can still
end up negative and why that is shown rather than prevented.
**WHY A CORRECTION IS ANOTHER ROW.** Editing the amount would leave one line saying what
happened, which is the outcome `CloseShiftAsync` refuses for the end-of-shift count: a
figure that can be silently overwritten stops being evidence. So a mistake produces a second row
pointing at the first, and the drawer carries both.

## if

```csharp
if (amountKopecks < 0)
```

0 IS ALLOWED AND IS NOT A NO-OP. A café whose change belongs to the owner opens with an
explicit 0 rather than with nothing: "I counted, there is nothing of mine in there" and
"nobody has opened the till yet" are different facts and the second one is the one worth
catching. Writing the row costs nothing and keeps every shift uniform.

## if

```csharp
if (amountKopecks > ledger.InDrawerKopecks)
```

The refusal names BOTH figures. "Insufficient funds" against a drawer the operator cannot
see is unactionable, and the whole value of checking here is that the operator is told what
the app believes is in there before they go and count it. TextFormat.Money and not
Money.FromKopecks: a raw decimal prints "100.01", which is not how any of this is written
anywhere a human reads it.

## if

```csharp
if (original.ShiftId != shift.Id)
```

Same shift, checked explicitly rather than by the absence of a shift filter: a correcting
entry belongs to the drawer the mistake was made in, and cancelling a movement out of a
shift that is not open would put the two rows in different places for one mistake.

## opposite

```csharp
var opposite = original.Kind == CashMovementKind.Float ? CashMovementKind.Payout : CashMovementKind.Float;
```

The opposite kind, NOT the same kind with a link. What physically happened is the point of
the row: cancelling a payout put money INTO the drawer, and recording that as a reversed
payout would say the opposite about which way it went while still netting correctly. A
reader summing the column by hand has to get the same answer the code does.

## RequireActiveShiftAsync

```csharp
private static async Task<Shift> RequireActiveShiftAsync(AppDbContext db, CancellationToken cancellationToken)
```

The message names the fix, because "no shift" is the answer an operator gets for two quite
different situations: a terminal that was never opened today, and one that was closed a
moment ago. Both need "open the shift", so both get it said.

## NormaliseReason

```csharp
private static string? NormaliseReason(string? reason, bool required)
```

Truncation is refused for the reason `CloseShiftAsync` refuses it: the string is the
record, and shortening what someone wrote still looks like a complete answer while being a
different sentence. Deliberately inconsistent with `PaymentRecorder.TruncateNote`, which
does shorten — there the money and the order it belongs to are recorded elsewhere, here
nothing else preserves what was said.

## IShiftLedger

```csharp
public interface IShiftLedger
```

The smallest of the five ports and the one that mattered most to carve out. Before the split,
`ShiftAnalyticsViewModel` — a screen that only reads — held an interface that offered
`CloseShiftAsync`, and `BackupService` held one that offered `RefundAsync`. Nothing
stopped either. Now the closing screen needs this port, and this port is the only place a shift
can be closed, so the two facts cannot drift apart.
The cashier-facing rules — the opening float is recorded, the close reconciles, no next shift is
opened — are documented on the methods themselves rather than restated here.

## GetActiveShiftAsync

```csharp
Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken = default);
```

Null is a NORMAL state, not an error: a shift is opened deliberately now
(`OpenShiftAsync`), so a terminal that has not been opened today, or was closed a
moment ago, reports it as absent. It used to be an error — this call created a shift instead —
which meant the opening float could never be recorded: the shift already existed by the time
anybody was asked about the change in the drawer.

## OpenShiftAsync

```csharp
Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default);
```

Opens a shift and records the change put into the drawer to start it.
<param name="floatKopecks">Change in the drawer at the opening, kopecks. May be 0 and may never
be negative: a café whose change belongs to the owner opens with an explicit zero, which is a
different and more useful fact than an absent float.</param>
<param name="reason">Optional note on the float; see `ICashLedgerService.RecordFloatAsync`.</param>

REFUSES when a shift is already open. Two overlapping shifts would each have their own float,
their own orders and their own count, and the drawer they describe would be the sum of two
tills — the reconciliation would then balance against a figure nobody could have counted.
There is no float parameter that must be positive and no way to open a shift without saying
what is in the drawer, because "I opened the till and counted it" is the one statement the
end-of-shift count is later compared against.

## GetLastCountedCashKopecksAsync

```csharp
Task<long?> GetLastCountedCashKopecksAsync(CancellationToken cancellationToken = default);
```

The COUNTED figure, not the expected one, because what carries over is what was physically
there. Null when there is no earlier shift, or when the last one was closed without ever being
counted — in which case there is nothing to suggest and the operator types it.

## GetLatestShiftAsync

```csharp
Task<Shift?> GetLatestShiftAsync(CancellationToken cancellationToken = default);
```

Null only on a terminal that has never had a shift, which is a demo seed rather than a real
state. Exists so the backup does not reach into the DbContext to decide which shift a report
is about — that choice is a shift question and belongs with the shift rules.

## CloseShiftAsync

```csharp
Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default);
```

NO NEXT SHIFT IS OPENED. It used to be, because a shift had to exist before anything could be
sold; a shift is now opened on purpose with its change recorded, so closing leaves the terminal
with no open shift and the opening screen is what comes next.
Reconciling is MANDATORY: a shift cannot be closed without entering what was counted, and a
mismatch has to carry a reason. <paramref name="countedCashKopecks"/> is kopecks, may be 0
(an empty drawer is the case that matters most, not an absent count) and may never be
negative. <paramref name="discrepancyReason"/> is required iff the count differs from the
expectation, accepted but never required when it matches, and rejected rather than truncated
when it is over-long — it is a mandatory audit field.
The expectation is `CashLedger`'s figure: the opening float, plus the cash from orders,
less the cash handed back and less what was carried away. A count taken against the payments
alone would report a shortage of exactly the change that was in the till all day.
There is NO shiftId parameter, deliberately: the service resolves the active shift itself.
A caller naming it could reconcile one shift and have the service close another.
A count, once recorded, is NOT re-editable — there is no path that rewrites these four
columns. A silently overwritten count is worse than no count. Recounting, if it is ever
wanted, needs an append-only `CashCounts(ShiftId, CountedAt, CashKopecks, Reason)`
table and a report that reads both.

## ShiftPayments

```csharp
internal static class ShiftPayments
```

Money through one till during one shift: what was collected and what was given back, each split
by method. Shared by the shift report (`GetShiftStatsAsync`) and the CSV export so the two
cannot grow different definitions of "принято оплат". Grouping happens in SQL: a shift holds a
bounded number of payments, but the alternative — loading every row and folding in memory — is
the shape that degrades into a per-row round trip once a real day of data is in the table.
Revenue is deliberately NOT part of this: the shift report keeps counting
`SUM(Orders.TotalKopecks)` over completed orders, so a day's revenue never depends on
payments having been recorded.
The join carries NO status filter, and that is exactly right rather than an oversight: a refund
taken during the next shift still belongs to the drawer the money left. Booking it into the new
shift would be the lie — the new drawer never held the cash.
WHAT THIS IS NOT: the drawer. It never was, once a till can be opened with change in it, and
`CashLedger` is where that figure lives.

## PaymentsOnlyKopecks

```csharp
public long PaymentsOnlyKopecks => CashKopecks - RefundsCashKopecks;
```

Cash taken in, less cash handed back — the payments side of the drawer figure ONLY.
It is NOT the drawer, and it no longer offers one. It was, until the opening float and
collection existed, and the difference is the whole reason `CashLedger`
exists: this pair of numbers is right for a till opened empty and never emptied, and wrong
the moment anybody puts change in or carries cash out. Anything a human will read comes
from `CashLedger.Totals.InDrawerKopecks`.
What this pair CAN guarantee is non-negativity. The join above takes this shift's payments
and this shift's refunds from the shift's OWN orders, so the per-order per-method
inequality `refunded &lt;= collected` that
`PaymentRecorder.AllocateMirroredSlices` guarantees sums to the same inequality for
the shift. A refund pressed during a later shift on an order belonging to this one is
still this shift's refund — that is the attribution the join performs — so it lowers the
figure exactly as much as it raised it, and never below zero. The drawer as a whole has
no such guarantee; see `CashLedger`.

## ShiftReconciliation

```csharp
internal static class ShiftReconciliation
```

The one place that turns the four stored reconciliation columns of a `Shift` into a
`CashReconciliation`. Shared by the shift report and the CSV export so that both say
the same thing about "was this shift counted, and against what".
The mapping is deliberately trivial, because the interesting decisions were made when the
columns were written: both money figures are present together or not at all, and they are frozen
rather than recomputed.

## Read

```csharp
public static CashReconciliation? Read(Shift? shift)
```

The recorded count of <paramref name="shift"/>, or null when it was never counted — which
covers every shift that predates the feature. NOT null and NOT zero for a counted empty
drawer: a count of 0 against a non-zero expectation is missing money, and it is the one case
the whole feature exists to catch.

## shift

```csharp
shift.ReconciledAt ?? shift.EndTime ?? shift.StartTime,
```

"Recorded at", never "counted at": ReconciledAt is the moment the close was saved, which
is not the moment the drawer was physically counted — the column says when the fact
became part of the record. It equals EndTime to the second, and nothing derives from
it. The fallbacks only fire on a hand-edited row: a count is still reported rather
than dropped, because dropping it would hide the only evidence the drawer was counted.

## IShiftSession

```csharp
public interface IShiftSession
```

A CACHE, and that is the whole reason it exists rather than a direct query at every call site.
Navigation is synchronous — `Shell.OnNavigating` cannot await — so the answer has to already
be known when the question is asked. It is refreshed at startup, and after every operation that
can change the answer: opening, closing, and a restore from backup.
It holds NO state a reader should trust for money. `GetActiveShiftAsync` on
`IOrderService` is the authority for anything about a shift's contents; this answers
one question, "may the operator use the till at all", and it answers it from the database whenever
it is asked to re-read.
