using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

public sealed class ReportExportService(IDbContextFactory<AppDbContext> factory) : IReportExportService
{
    private static readonly string[] SalesHeader =
        ["Дата", "Смена", "Заказ", "Статус", "Позиция", "Вариант", "Модификатор", "Кол-во", "Цена", "Сумма"];

    public async Task<string> ExportSalesCsvAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = (await db.OrderItems.AsNoTracking()
            .Join(db.Orders.AsNoTracking(), item => item.OrderId, order => order.Id, (item, order) => new
            {
                order.OrderNumber,
                order.CreatedAt,
                order.Status,
                item.ProductName,
                item.SelectedModifierName,
                item.SelectedVariantName,
                item.Quantity,
                item.PriceKopecks,
                item.LineTotalKopecks
            })
            .ToListAsync(cancellationToken))
            // SQLite cannot ORDER BY a DateTimeOffset column in SQL — sort in memory.
            .OrderByDescending(row => row.CreatedAt)
            .ThenBy(row => row.OrderNumber)
            .ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine(string.Join(Csv.Delimiter, SalesHeader));

        foreach (var row in rows)
        {
            builder.AppendLine(Csv.Join(
                row.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                string.Empty,
                row.OrderNumber.ToString(),
                row.Status.ToString(),
                row.ProductName,
                row.SelectedVariantName,
                row.SelectedModifierName,
                row.Quantity.ToString(),
                Money.FromKopecks(row.PriceKopecks).ToString("F2"),
                Money.FromKopecks(row.LineTotalKopecks).ToString("F2")));
        }

        return builder.ToString();
    }

    public async Task<string> ExportShiftReportCsvAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.ShiftId == shiftId)
            .ToListAsync(cancellationToken);
        orders = orders.OrderBy(order => order.CreatedAt).ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine(Csv.Join("Заказ", "Создан", "Готов", "Закрыт", "Статус", "Позиций", "Сумма"));

        foreach (var order in orders)
        {
            builder.AppendLine(Csv.Join(
                order.OrderNumber.ToString(),
                order.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                order.ReadyAt?.ToLocalTime().ToString("HH:mm:ss"),
                order.CompletedAt?.ToLocalTime().ToString("HH:mm:ss"),
                order.Status.ToString(),
                order.Items.Sum(item => item.Quantity).ToString(),
                order.TotalPrice.ToString("F2")));
        }

        var completed = orders.Where(order => order.Status == OrderStatus.Completed).ToList();
        var revenue = completed.Sum(order => order.TotalPrice);

        // "Принято оплат" — what the till recorded, next to the revenue figure above. Exported on
        // purpose: the shift report is what a manager takes to the cash count, and without these
        // lines the drawer total cannot be reconciled against the day on paper. The wording and the
        // GROSS meaning are deliberately untouched: every existing reading of "Принято …" keeps
        // meaning "what came in".
        var payments = await ShiftPayments.ReadAsync(db, shiftId, cancellationToken);

        builder.AppendLine();
        builder.AppendLine(Csv.Join("Итого чеков", completed.Count.ToString()));
        builder.AppendLine(Csv.Join("Выручка", revenue.ToString("F2")));
        builder.AppendLine(Csv.Join("Отменено", orders.Count(order => order.Status == OrderStatus.Cancelled).ToString()));
        builder.AppendLine(Csv.Join("Принято оплат", payments.Count.ToString()));
        builder.AppendLine(Csv.Join("Принято наличными", Money.FromKopecks(payments.CashKopecks).ToString("F2")));
        builder.AppendLine(Csv.Join("Принято картой", Money.FromKopecks(payments.CardKopecks).ToString("F2")));
        builder.AppendLine(Csv.Join("Принято всего", Money.FromKopecks(payments.TotalKopecks).ToString("F2")));

        // What went back out, by the method it left in. Reported separately from "Принято" rather
        // than subtracted into it: the gross figure is what the till took, and quietly lowering it
        // would make a day with refunds look like a day that took less.
        builder.AppendLine(Csv.Join("Возвращено наличными", Money.FromKopecks(payments.RefundsCashKopecks).ToString("F2")));
        builder.AppendLine(Csv.Join("Возвращено картой", Money.FromKopecks(payments.RefundsCardKopecks).ToString("F2")));
        builder.AppendLine(Csv.Join("Возвращено всего", Money.FromKopecks(payments.RefundedKopecks).ToString("F2")));

        // The one line a manager counts against the drawer, so it is the last word in the export and
        // the only one that nets out. Anything ambiguous about it defeats the whole point of taking
        // the report to the till.
        builder.AppendLine(Csv.Join("Итого наличными в кассе", Money.FromKopecks(payments.CashInDrawerKopecks).ToString("F2")));

        // The count itself, printed last because it is the manager's own answer to the drawer line
        // above. The shift row is loaded here for the first time: the export used to read orders
        // only, so it had nothing to say about the money after it left the drawer.
        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == shiftId, cancellationToken);
        AppendReconciliation(builder, ShiftReconciliation.Read(shift), payments.CashInDrawerKopecks);

        return builder.ToString();
    }

    /// <summary>
    /// The cash count block. Two rules, both of them about not lying:
    /// <list type="bullet">
    /// <item>Every "expected" figure carries the MOMENT it was taken at. A bare "Ожидалось" line is
    /// the ambiguity the feature exists to remove — read a week later it is a claim about some
    /// unspecified drawer, and an auditor cannot use it.</item>
    /// <item>A shift that was never counted says exactly that, in one line, and prints no numbers at
    /// all. A historical export must never be readable as "the drawer came out at 0" — that is a
    /// fabricated count on precisely the rows somebody checks first.</item>
    /// </list>
    /// </summary>
    private static void AppendReconciliation(
        System.Text.StringBuilder builder,
        CashReconciliation? reconciliation,
        long expectedNowKopecks)
    {
        if (reconciliation is null)
        {
            builder.AppendLine(Csv.Join("Пересчёт", "не проводился"));
            return;
        }

        builder.AppendLine(Csv.Join($"Пересчёт кассы ({reconciliation.CountedAt.ToLocalTime():HH:mm})"));
        builder.AppendLine(Csv.Join("Ожидалось на момент закрытия", Money.FromKopecks(reconciliation.ExpectedKopecks).ToString("F2")));
        builder.AppendLine(Csv.Join("Пересчитано в кассе", Money.FromKopecks(reconciliation.CountedKopecks).ToString("F2")));

        // One line for the difference, worded as the drawer is actually wrong. A matching count
        // prints nothing here: the two lines above already say "same", and "Совпадает" would be a
        // fourth thing to keep in step.
        if (reconciliation.Difference == CashDifference.Shortage)
            builder.AppendLine(Csv.Join("Не хватает", Money.FromKopecks(-reconciliation.DiscrepancyKopecks).ToString("F2")));
        else if (reconciliation.Difference == CashDifference.Overage)
            builder.AppendLine(Csv.Join("Излишек", Money.FromKopecks(reconciliation.DiscrepancyKopecks).ToString("F2")));

        if (!string.IsNullOrWhiteSpace(reconciliation.Reason))
            builder.AppendLine(Csv.Join("Причина", reconciliation.Reason));

        // Money that left the drawer AFTER the count was recorded — a refund against a closed shift,
        // which is allowed precisely because the cash physically belonged to that drawer. Printed
        // only when there is any: at zero the pair would repeat the drawer line above in other
        // words. The value can only be non-negative for a counted shift, since the only thing that
        // can lower a closed shift's live cash figure is a refund.
        var drift = reconciliation.ExpectedKopecks - expectedNowKopecks;
        if (drift != 0)
        {
            builder.AppendLine(Csv.Join("Учтено возвратов после пересчёта", Money.FromKopecks(drift).ToString("F2")));
            builder.AppendLine(Csv.Join("Ожидается сейчас", Money.FromKopecks(expectedNowKopecks).ToString("F2")));
        }
    }
}
