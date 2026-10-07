using System.Text;
using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>CSV import/export of the catalogue.</summary>
public sealed partial class CatalogService
{
    private static readonly string[] ProductsHeader =
        ["Название", "Цена", "Раздел", "Аллергены", "Теги", "Доступен с", "Доступен до", "В наличии"];

    public async Task<string> ExportProductsCsvAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var products = await db.Products.AsNoTracking()
            .Include(product => product.Category)
            .Where(product => !product.IsDeleted)
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(Csv.Delimiter, ProductsHeader));

        foreach (var product in products)
        {
            builder.AppendLine(Csv.Join(
                product.Name,
                product.Price.ToString("F2"),
                product.Category?.Name,
                product.Allergens,
                product.Tags,
                product.AvailableFromHour?.ToString(),
                product.AvailableToHour?.ToString(),
                product.IsAvailable ? "1" : "0"));
        }

        return builder.ToString();
    }

    /// <summary>Imports products, updating existing rows with the same name instead of creating duplicates (the previous implementation always inserted, so a repeated import doubled the catalogue).</summary>

    public async Task<ProductImportResult> ImportProductsCsvAsync(string csvContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csvContent)) throw new ValidationFailureException("Файл пуст.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var categories = await db.Categories.ToDictionaryAsync(category => category.Name, StringComparer.CurrentCultureIgnoreCase, cancellationToken);
        var products = await db.Products.ToDictionaryAsync(product => product.Name, StringComparer.CurrentCultureIgnoreCase, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var warnings = new List<string>();
        var rows = Csv.Parse(csvContent);

        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Length < 2)
            {
                skipped++;
                continue;
            }

            var name = row[0].Trim().TrimStart('\uFEFF');
            if (string.IsNullOrEmpty(name))
            {
                skipped++;
                continue;
            }

            if (!TextFormat.TryParseDecimal(row[1].Trim(), out var price) || price < 0)
            {
                warnings.Add($"Строка {index + 1}: не удалось прочитать цену «{row[1]}», товар «{name}» пропущен.");
                skipped++;
                continue;
            }

            var categoryName = row.Length > 2 ? row[2].Trim() : string.Empty;
            Category? category = null;
            if (!string.IsNullOrEmpty(categoryName))
            {
                if (!categories.TryGetValue(categoryName, out category))
                {
                    category = new Category { Id = Guid.NewGuid(), Name = categoryName };
                    db.Categories.Add(category);
                    categories[categoryName] = category;
                }
            }

            var allergens = GetField(row, 3);
            var tags = GetField(row, 4);
            var availableFrom = ParseHour(GetField(row, 5));
            var availableTo = ParseHour(GetField(row, 6));
            var isAvailable = GetField(row, 7) is not "0";

            if (products.TryGetValue(name, out var existing))
            {
                if (existing.PriceKopecks != Money.ToKopecks(price))
                {
                    db.PriceHistoryEntries.Add(new PriceHistoryEntry
                    {
                        Id = Guid.NewGuid(),
                        ProductId = existing.Id,
                        OldPriceKopecks = existing.PriceKopecks,
                        NewPriceKopecks = Money.ToKopecks(price),
                        ChangedAt = now,
                        Reason = "Импорт из CSV"
                    });
                }

                existing.Price = price;
                existing.Category = category;
                existing.Allergens = allergens;
                existing.Tags = tags;
                existing.AvailableFromHour = availableFrom;
                existing.AvailableToHour = availableTo;
                existing.IsAvailable = isAvailable;
                existing.IsDeleted = false;
                existing.DeletedAt = null;
                updated++;
                continue;
            }

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = name,
                Price = price,
                Category = category,
                Allergens = allergens,
                Tags = tags,
                AvailableFromHour = availableFrom,
                AvailableToHour = availableTo,
                IsAvailable = isAvailable
            };
            db.Products.Add(product);
            products[name] = product;
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Catalogue import finished: {Created} created, {Updated} updated, {Skipped} skipped", created, updated, skipped);
        return new ProductImportResult(created, updated, skipped, warnings);
    }

    private static string? GetField(string[] row, int index) =>
        row.Length > index && !string.IsNullOrWhiteSpace(row[index]) ? row[index].Trim() : null;

    private static int? ParseHour(string? value) =>
        int.TryParse(value, out var hour) && hour is >= 0 and <= 23 ? hour : null;
}
