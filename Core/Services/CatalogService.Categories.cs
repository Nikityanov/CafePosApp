using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// One half of the catalogue service: categories.
/// </summary>
/// <remarks>
/// The class is already partial and was already 416 lines with 24 methods over five aggregates.
/// Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
/// to the primary constructor or to DI. Only this part declares the constructor and the
/// interface - the others repeat neither.
/// </remarks>
public sealed partial class CatalogService
{
    // ─── Categories ───

    public async Task SaveCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category.Name)) throw new ValidationFailureException("Укажите название раздела.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Categories.FirstOrDefaultAsync(item => item.Id == category.Id, cancellationToken);
        if (existing is null) db.Categories.Add(category);
        else existing.Name = category.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var category = await db.Categories.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (category is null) return;
        db.Categories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

}
