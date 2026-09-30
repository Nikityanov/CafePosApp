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
        builder.AppendLine();
        builder.AppendLine(Csv.Join("Итого чеков", completed.Count.ToString()));
        builder.AppendLine(Csv.Join("Выручка", revenue.ToString("F2")));
        builder.AppendLine(Csv.Join("Отменено", orders.Count(order => order.Status == OrderStatus.Cancelled).ToString()));

        return builder.ToString();
    }
}
