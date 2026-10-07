# Тесты: касса и сверка

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## CashLedgerTests

```csharp
public class CashLedgerTests
```

These rules did not exist before the movements table, and each one here is a decision that would
otherwise have been made by accident:
<list type="bullet">
<item>a shift is opened, never created on the first sale;</item>
<item>the opening change is part of what the drawer is expected to hold, or the end-of-shift
count reports a shortage of exactly the change;</item>
<item>money cannot be taken out of the drawer unless the drawer says it is there;</item>
<item>a closed shift accepts nothing further — which is the price of the write-once columns it
already had;</item>
<item>a mistake is answered with a second row, never by editing the first.</item>
</list>
